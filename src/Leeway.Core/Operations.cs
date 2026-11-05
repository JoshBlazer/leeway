using System.Text.Json;

namespace Leeway.Core;

/// <summary>What an operation does.</summary>
public enum OperationKind
{
    /// <summary>Merges a delta into an entity's fields.</summary>
    Merge,

    /// <summary>Asks for a lease on an escrowed quantity.</summary>
    LeaseRequest,

    /// <summary>Reports a spend already confirmed on the device, from its lease.</summary>
    Spend,

    /// <summary>Asks to spend beyond the lease. The server decides.</summary>
    ProvisionalSpend,

    /// <summary>Gives unused lease back.</summary>
    ReturnLease,
}

/// <summary>What the server did with an operation.</summary>
public enum OutcomeStatus
{
    /// <summary>The delta was merged.</summary>
    Merged,

    /// <summary>A lease was granted. The amount may be zero.</summary>
    LeaseGranted,

    /// <summary>A confirmed spend was recorded against the lease.</summary>
    SpendConfirmed,

    /// <summary>A provisional spend was funded from the pool.</summary>
    ProvisionalAccepted,

    /// <summary>A provisional spend could not be funded, and nothing changed.</summary>
    ProvisionalRejected,

    /// <summary>Lease was returned.</summary>
    LeaseReturned,

    /// <summary>A confirmed spend exceeded what the device holds, as after a write-off. It is a reported violation.</summary>
    Overdraft,
}

/// <summary>One unit of intent from a device. The ID makes a replay harmless.</summary>
/// <param name="Id">A globally unique ID.</param>
/// <param name="Device">The device that made it.</param>
/// <param name="Sequence">Increases with each operation from this device.</param>
/// <param name="Stamp">The device's hybrid time when made.</param>
/// <param name="Observed">The highest server stamp the device had seen when made.</param>
/// <param name="Kind">What it does.</param>
/// <param name="EntityType">The entity type.</param>
/// <param name="EntityId">The entity ID.</param>
/// <param name="Delta">For a merge, the delta.</param>
/// <param name="Resource">For an escrow operation, the resource.</param>
/// <param name="Amount">For an escrow operation, the amount.</param>
/// <param name="SettledBelow">Every earlier operation from this device below this sequence has been acknowledged, so it will not arrive again.</param>
public sealed record Operation(
    string Id,
    string Device,
    long Sequence,
    HybridTimestamp Stamp,
    HybridTimestamp Observed,
    OperationKind Kind,
    string EntityType,
    string EntityId,
    MergeDocument? Delta = null,
    string? Resource = null,
    long Amount = 0,
    long SettledBelow = 0)
{
    /// <summary>Serializes the operation.</summary>
    /// <returns>JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Deserializes an operation.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The operation.</returns>
    public static Operation FromJson(string json)
    {
        Operation op = JsonSerializer.Deserialize<Operation>(json) ?? throw new JsonException("Empty operation.");
        return op.Delta is null ? op : op with { Delta = MergeDocument.FromJson(op.Delta.ToJson()) };
    }
}

/// <summary>The result of applying an operation.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Amount">The amount involved, if any.</param>
public sealed record OperationOutcome(OutcomeStatus Status, long Amount = 0);

/// <summary>A device's durable log: what has been done, and what the server has acknowledged.</summary>
public sealed class OperationLog
{
    private readonly Dictionary<string, Operation> operations = [];
    private readonly Dictionary<string, OperationOutcome> outcomes = [];

    /// <summary>Operations not yet acknowledged, oldest first.</summary>
    public IReadOnlyList<Operation> Pending
        => this.operations.Values.Where(op => !this.outcomes.ContainsKey(op.Id)).OrderBy(op => op.Sequence).ToList();

    /// <summary>Adds an operation.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>False if an operation with this ID is already in the log.</returns>
    public bool Append(Operation operation) => this.operations.TryAdd(operation.Id, operation);

    /// <summary>Records an acknowledgement. The first outcome stands, because a replay returns the same one.</summary>
    /// <param name="id">The operation ID.</param>
    /// <param name="outcome">The outcome.</param>
    public void Acknowledge(string id, OperationOutcome outcome) => this.outcomes.TryAdd(id, outcome);

    /// <summary>Discards an operation. Only for simulating a broken client.</summary>
    /// <param name="id">The operation ID.</param>
    public void Forget(string id) => this.operations.Remove(id);

    /// <summary>Reads an operation's outcome.</summary>
    /// <param name="id">The operation ID.</param>
    /// <returns>The outcome, or null if not yet acknowledged.</returns>
    public OperationOutcome? OutcomeOf(string id) => this.outcomes.GetValueOrDefault(id);
}
