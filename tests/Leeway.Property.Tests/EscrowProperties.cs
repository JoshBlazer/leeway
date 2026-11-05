using FsCheck.Xunit;
using Leeway.Core;

namespace Leeway.Property.Tests;

/// <summary>M1 exit check: escrow conservation, over arbitrary sequences of ledger operations.</summary>
public class EscrowProperties
{
    private static string Device(byte b) => "d" + (b % 4);

    private static IEnumerable<EscrowLedger> Replay(ushort total, (byte Kind, byte Device, byte Amount)[] ops)
    {
        EscrowLedger ledger = EscrowLedger.Create("r", total % 500);
        IAllocationPolicy policy = new FixedLease(10);
        yield return ledger;
        foreach ((byte kind, byte device, byte amount) in ops ?? [])
        {
            ledger = (kind % 5) switch
            {
                0 => ledger.RequestLease(Device(device), amount, policy).Ledger,
                1 => ledger.Spend(Device(device), amount).Ledger,
                2 => ledger.SpendProvisional(Device(device), amount).Ledger,
                3 => ledger.Return(Device(device), amount).Ledger,
                _ => ledger.WriteOff(Device(device)),
            };
            yield return ledger;
        }
    }

    [Property]
    public bool The_total_is_conserved_after_every_operation(ushort total, (byte, byte, byte)[] ops)
        => Replay(total, ops).All(l => l.IsConserved);

    [Property]
    public bool Consumption_never_exceeds_the_total_and_nothing_goes_negative(ushort total, (byte, byte, byte)[] ops)
        => Replay(total, ops).All(l => l.Consumed <= l.Total && l.Remaining >= 0 && l.Unleased >= 0 && l.Accounts.Values.All(a => a.Held >= 0));

    [Property]
    public bool An_honest_wallet_never_produces_an_overdraft_in_any_report_order(byte grant, byte[] spends, int shuffle)
    {
        EscrowLedger ledger = EscrowLedger.Create("r", 1_000);
        (ledger, OperationOutcome granted) = ledger.RequestLease("d", grant, new FixedLease(grant));

        Wallet wallet = new(granted.Amount, 0, 0);
        List<long> confirmed = [];
        foreach (byte amount in spends ?? [])
        {
            if (wallet.TrySpend(amount) is { } next)
            {
                wallet = next;
                confirmed.Add(amount);
            }
        }

        Random random = new(shuffle);
        foreach (long amount in confirmed.OrderBy(_ => random.Next()))
        {
            (ledger, OperationOutcome outcome) = ledger.Spend("d", amount);
            if (outcome.Status != OutcomeStatus.SpendConfirmed)
            {
                return false;
            }
        }

        return ledger.Accounts["d"].Overdraft == 0 && ledger.Accounts["d"].Spent == wallet.Spent;
    }

    [Property]
    public bool A_wallet_never_spends_more_than_it_holds(byte grant, byte[] spends)
    {
        Wallet wallet = new(grant, 0, 0);
        foreach (byte amount in spends ?? [])
        {
            wallet = wallet.TrySpend(amount) ?? wallet;
            if (wallet.Available < 0)
            {
                return false;
            }
        }

        return wallet.Spent <= grant;
    }

    [Property]
    public bool A_ledger_survives_a_json_round_trip(ushort total, (byte, byte, byte)[] ops)
    {
        EscrowLedger last = Replay(total, ops).Last();
        return EscrowLedger.FromJson(last.ToJson()).Equals(last);
    }

    [Fact]
    public void A_provisional_spend_is_funded_from_the_pool_and_leaves_the_lease_alone()
    {
        EscrowLedger ledger = EscrowLedger.Create("r", 12);
        (ledger, _) = ledger.RequestLease("d", 10, new FixedLease(10));

        (ledger, OperationOutcome first) = ledger.SpendProvisional("d", 2);
        Assert.Equal(OutcomeStatus.ProvisionalAccepted, first.Status);
        Assert.Equal(10, ledger.Accounts["d"].Held);

        (ledger, OperationOutcome second) = ledger.SpendProvisional("d", 1);
        Assert.Equal(OutcomeStatus.ProvisionalRejected, second.Status);
        Assert.Equal(10, ledger.Remaining);
        Assert.True(ledger.IsConserved);
    }

    [Fact]
    public void A_spend_after_a_write_off_is_recorded_as_an_overdraft_not_hidden()
    {
        EscrowLedger ledger = EscrowLedger.Create("r", 10);
        (ledger, _) = ledger.RequestLease("lost", 10, new FixedLease(10));
        ledger = ledger.WriteOff("lost");
        Assert.Equal(10, ledger.Unleased);

        (ledger, OperationOutcome outcome) = ledger.Spend("lost", 4);

        Assert.Equal(OutcomeStatus.Overdraft, outcome.Status);
        Assert.Equal(4, ledger.Accounts["lost"].Overdraft);
        Assert.Equal(0, ledger.Consumed);
        Assert.True(ledger.IsConserved);
    }

    [Fact]
    public void Equal_split_gives_each_expected_device_its_share_and_respects_the_floor()
    {
        EqualSplit policy = new(4, Floor: 2);
        EscrowLedger ledger = EscrowLedger.Create("r", 41);
        (ledger, OperationOutcome first) = ledger.RequestLease("a", 0, policy);
        Assert.Equal(10, first.Amount);

        // A device that already holds its share asks again: it gets the floor.
        (ledger, OperationOutcome again) = ledger.RequestLease("a", 0, policy);
        Assert.Equal(2, again.Amount);

        // Never more than the pool holds.
        EscrowLedger small = EscrowLedger.Create("s", 1);
        (_, OperationOutcome limited) = small.RequestLease("a", 0, policy);
        Assert.Equal(1, limited.Amount);
    }

    [Property]
    public bool The_operation_log_ignores_duplicates_and_keeps_the_first_outcome(byte count)
    {
        OperationLog log = new();
        Operation Make(int i) => new($"op{i}", "d", i, default, default, OperationKind.Merge, "T", "e");
        for (int i = 0; i < count % 20; i++)
        {
            if (!log.Append(Make(i)) || log.Append(Make(i)))
            {
                return false;
            }
        }

        bool ok = log.Pending.Count == count % 20;
        if (count % 20 > 0)
        {
            log.Acknowledge("op0", new OperationOutcome(OutcomeStatus.Merged));
            log.Acknowledge("op0", new OperationOutcome(OutcomeStatus.Overdraft));
            ok &= log.OutcomeOf("op0")!.Status == OutcomeStatus.Merged && log.Pending.Count == (count % 20) - 1;
        }

        return ok;
    }
}
