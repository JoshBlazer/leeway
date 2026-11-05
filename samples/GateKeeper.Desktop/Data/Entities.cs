using Leeway.Core;
using Leeway.Datasync.Client;

namespace GateKeeper.Desktop.Data;

/// <summary>The gate's copy of a zone. See docs/design/gatekeeper.md for what each attribute guarantees.</summary>
public class Zone : OfflineEntity
{
    public string Name { get; set; } = string.Empty;

    public int Capacity { get; set; }

    [Escrow(Min = 0, Lease = 4)]
    public int RemainingCapacity { get; set; }

    [Counter]
    public int CheckIns { get; set; }

    [LastWriterWins]
    public string? Notes { get; set; }

    [AddWinsSet]
    public string? Flags { get; set; }
}

/// <summary>The gate's copy of a ticket. More than one gate in ScannedAt means it was used twice.</summary>
public class Ticket : OfflineEntity
{
    public string ZoneId { get; set; } = string.Empty;

    [AddWinsSet]
    public string? ScannedAt { get; set; }
}

public enum ScanState
{
    Admitted,
    Provisional,
    TurnedAway,
}

/// <summary>This gate's record of what it did. Local only.</summary>
public class GateScan
{
    public string Id { get; set; } = string.Empty;

    public long Order { get; set; }

    public string TicketId { get; set; } = string.Empty;

    public string ZoneId { get; set; } = string.Empty;

    public ScanState State { get; set; }

    public string SpendOperationId { get; set; } = string.Empty;
}
