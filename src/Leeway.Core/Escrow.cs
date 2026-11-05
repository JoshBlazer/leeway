using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leeway.Core;

/// <summary>One device's position in an escrowed quantity. All figures are cumulative. See docs/design/escrow-protocol.md.</summary>
/// <param name="Granted">Total granted.</param>
/// <param name="Spent">Total spent from the lease.</param>
/// <param name="Returned">Total returned.</param>
/// <param name="ProvisionalAccepted">Total of provisional spends funded from the pool.</param>
/// <param name="WrittenOff">Total taken back by an admin.</param>
/// <param name="Overdraft">Total spent that could not be funded. A reported violation.</param>
public sealed record EscrowAccount(long Granted = 0, long Spent = 0, long Returned = 0, long ProvisionalAccepted = 0, long WrittenOff = 0, long Overdraft = 0)
{
    /// <summary>What the device still holds.</summary>
    [JsonIgnore]
    public long Held => Granted - Spent - Returned - WrittenOff;
}

/// <summary>Decides how much to grant.</summary>
public interface IAllocationPolicy
{
    /// <summary>Decides a grant. The ledger caps it at what is unleased.</summary>
    /// <param name="ledger">The current ledger.</param>
    /// <param name="device">The requesting device.</param>
    /// <param name="requested">The amount asked for, or zero to let the policy decide.</param>
    /// <returns>The amount to grant.</returns>
    long Decide(EscrowLedger ledger, string device, long requested);
}

/// <summary>Grants a fixed size, or less if less was asked for.</summary>
/// <param name="Size">The lease size.</param>
public sealed record FixedLease(long Size) : IAllocationPolicy
{
    /// <inheritdoc />
    public long Decide(EscrowLedger ledger, string device, long requested) => requested > 0 ? Math.Min(requested, Size) : Size;
}

/// <summary>Tops a device up to an equal share of the total, and always grants at least a floor.</summary>
/// <param name="ExpectedDevices">How many devices share the total.</param>
/// <param name="Floor">The smallest useful grant.</param>
public sealed record EqualSplit(int ExpectedDevices, long Floor) : IAllocationPolicy
{
    /// <inheritdoc />
    public long Decide(EscrowLedger ledger, string device, long requested)
    {
        long share = ledger.Total / Math.Max(1, ExpectedDevices);
        long held = ledger.Accounts.GetValueOrDefault(device)?.Held ?? 0;
        long target = requested > 0 ? Math.Min(requested, share) : share;
        return Math.Max(Floor, target - held);
    }
}

/// <summary>The server's record of a bounded quantity. See docs/design/escrow-protocol.md.</summary>
/// <param name="Resource">The resource name.</param>
/// <param name="Total">The quantity that can ever be consumed.</param>
/// <param name="Unleased">What no device holds.</param>
/// <param name="Accounts">Each device's position.</param>
public sealed record EscrowLedger(string Resource, long Total, long Unleased, ImmutableSortedDictionary<string, EscrowAccount> Accounts)
{
    /// <summary>The quantity consumed so far.</summary>
    [JsonIgnore]
    public long Consumed => Accounts.Values.Sum(a => a.Spent + a.ProvisionalAccepted);

    /// <summary>The quantity not yet consumed.</summary>
    [JsonIgnore]
    public long Remaining => Total - Consumed;

    /// <summary>Whether nothing has been created or lost.</summary>
    [JsonIgnore]
    public bool IsConserved => Total == Unleased + Accounts.Values.Sum(a => a.Held) + Consumed;

    /// <summary>Creates a ledger.</summary>
    /// <param name="resource">The resource name.</param>
    /// <param name="total">The quantity that can ever be consumed.</param>
    /// <returns>The ledger.</returns>
    public static EscrowLedger Create(string resource, long total) => new(resource, total, total, Maps.Empty<EscrowAccount>());

    /// <summary>Grants what the policy decides, capped at what is unleased.</summary>
    /// <param name="device">The device.</param>
    /// <param name="requested">The amount asked for, or zero to let the policy decide.</param>
    /// <param name="policy">The allocation policy.</param>
    /// <returns>The new ledger and the outcome.</returns>
    public (EscrowLedger Ledger, OperationOutcome Outcome) RequestLease(string device, long requested, IAllocationPolicy policy)
    {
        long amount = Math.Clamp(policy.Decide(this, device, requested), 0, Unleased);
        return (Update(device, a => a with { Granted = a.Granted + amount }, Unleased - amount), new OperationOutcome(OutcomeStatus.LeaseGranted, amount));
    }

    /// <summary>Records a spend confirmed on the device. What exceeds the holding is an overdraft.</summary>
    /// <param name="device">The device.</param>
    /// <param name="amount">The amount.</param>
    /// <returns>The new ledger and the outcome.</returns>
    public (EscrowLedger Ledger, OperationOutcome Outcome) Spend(string device, long amount)
    {
        long funded = Math.Min(Math.Max(amount, 0), Account(device).Held);
        long overdraft = Math.Max(amount, 0) - funded;
        return (
            Update(device, a => a with { Spent = a.Spent + funded, Overdraft = a.Overdraft + overdraft }, Unleased),
            overdraft > 0 ? new OperationOutcome(OutcomeStatus.Overdraft, overdraft) : new OperationOutcome(OutcomeStatus.SpendConfirmed, funded));
    }

    /// <summary>Funds a spend beyond the lease from the pool, all or nothing. It never touches the device's holding.</summary>
    /// <param name="device">The device.</param>
    /// <param name="amount">The amount.</param>
    /// <returns>The new ledger and the outcome.</returns>
    public (EscrowLedger Ledger, OperationOutcome Outcome) SpendProvisional(string device, long amount)
    {
        if (amount < 0 || amount > Unleased)
        {
            return (this, new OperationOutcome(OutcomeStatus.ProvisionalRejected, Math.Max(amount, 0)));
        }

        return (Update(device, a => a with { ProvisionalAccepted = a.ProvisionalAccepted + amount }, Unleased - amount), new OperationOutcome(OutcomeStatus.ProvisionalAccepted, amount));
    }

    /// <summary>Takes back unused lease, up to what the device holds.</summary>
    /// <param name="device">The device.</param>
    /// <param name="amount">The amount.</param>
    /// <returns>The new ledger and the outcome.</returns>
    public (EscrowLedger Ledger, OperationOutcome Outcome) Return(string device, long amount)
    {
        long returned = Math.Clamp(amount, 0, Account(device).Held);
        return (Update(device, a => a with { Returned = a.Returned + returned }, Unleased + returned), new OperationOutcome(OutcomeStatus.LeaseReturned, returned));
    }

    /// <summary>An admin takes back a device's holding. If the device was only offline, its spends become overdrafts (ADR-0005).</summary>
    /// <param name="device">The device.</param>
    /// <returns>The new ledger.</returns>
    public EscrowLedger WriteOff(string device)
    {
        long held = Account(device).Held;
        return Update(device, a => a with { WrittenOff = a.WrittenOff + held }, Unleased + held);
    }

    /// <summary>Serializes the ledger.</summary>
    /// <returns>JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Deserializes a ledger.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The ledger.</returns>
    public static EscrowLedger FromJson(string json)
    {
        EscrowLedger raw = JsonSerializer.Deserialize<EscrowLedger>(json) ?? throw new JsonException("Empty ledger.");
        return raw with { Accounts = Maps.Normal(raw.Accounts) };
    }

    /// <inheritdoc />
    public bool Equals(EscrowLedger? other)
        => other is not null && Resource == other.Resource && Total == other.Total && Unleased == other.Unleased
            && Maps.SameItems(Accounts, other.Accounts, EqualityComparer<EscrowAccount>.Default);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Resource, Total, Unleased);

    private EscrowAccount Account(string device) => Accounts.GetValueOrDefault(device) ?? new EscrowAccount();

    private EscrowLedger Update(string device, Func<EscrowAccount, EscrowAccount> change, long unleased)
        => this with { Unleased = unleased, Accounts = Maps.Normal(Accounts).SetItem(device, change(Account(device))) };
}

/// <summary>
/// A device's view of its lease. Spent and Returned are the device's own record, and only Granted comes from the server,
/// so reordering and racing pulls cannot make it overspend.
/// </summary>
/// <param name="Granted">Total granted, as pulled from the server.</param>
/// <param name="Spent">Total this device has confirmed.</param>
/// <param name="Returned">Total this device has returned.</param>
public sealed record Wallet(long Granted, long Spent, long Returned)
{
    /// <summary>What can be spent right now, with no signal.</summary>
    public long Available => Granted - Spent - Returned;

    /// <summary>Spends if the lease covers it.</summary>
    /// <param name="amount">The amount.</param>
    /// <returns>The new wallet, or null if the lease does not cover it.</returns>
    public Wallet? TrySpend(long amount) => amount >= 0 && amount <= Available ? this with { Spent = Spent + amount } : null;

    /// <summary>Gives back up to the amount.</summary>
    /// <param name="amount">The amount wanted.</param>
    /// <returns>The new wallet and what was really given back.</returns>
    public (Wallet Wallet, long Amount) Return(long amount)
    {
        long returned = Math.Clamp(amount, 0, Available);
        return (this with { Returned = Returned + returned }, returned);
    }
}

/// <summary>Applies an escrow operation to a ledger. Used by the server and the simulator.</summary>
public static class EscrowRules
{
    /// <summary>Applies an operation.</summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="op">The operation.</param>
    /// <param name="policy">The allocation policy.</param>
    /// <returns>The new ledger and the outcome.</returns>
    public static (EscrowLedger Ledger, OperationOutcome Outcome) Apply(EscrowLedger ledger, Operation op, IAllocationPolicy policy) => op.Kind switch
    {
        OperationKind.LeaseRequest => ledger.RequestLease(op.Device, op.Amount, policy),
        OperationKind.Spend => ledger.Spend(op.Device, op.Amount),
        OperationKind.ProvisionalSpend => ledger.SpendProvisional(op.Device, op.Amount),
        OperationKind.ReturnLease => ledger.Return(op.Device, op.Amount),
        _ => throw new ArgumentException($"{op.Kind} is not an escrow operation.", nameof(op)),
    };
}
