using Leeway.Core;
using Leeway.Datasync.Server;

namespace GateKeeper.Server;

/// <summary>A part of the venue. See docs/design/gatekeeper.md for what each attribute guarantees.</summary>
public class Zone : LeewayEntity
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

/// <summary>A ticket. More than one gate in ScannedAt means it was used twice.</summary>
public class Ticket : LeewayEntity
{
    public string ZoneId { get; set; } = string.Empty;

    [AddWinsSet]
    public string? ScannedAt { get; set; }
}
