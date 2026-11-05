using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leeway.Core;

namespace Leeway.Simulation;

public enum EventType
{
    CounterAdd,
    RegisterWrite,
    SetAdd,
    SetRemove,
    Spend,
    ReturnLease,
    RequestLease,
    Push,
    Pull,
    Partition,
    Heal,
    Crash,
    Skew,
    Advance,
    WriteOff,
}

/// <summary>What goes wrong during a push or pull. See docs/design/simulation.md for the evidence behind each.</summary>
public enum Fault
{
    None,
    RequestLost,
    ResponseLost,
    Duplicate,
    Timeout,
    TimeoutAfterApply,
    MidBatchDrop,
}

/// <summary>One step of a schedule. Value means the amount, the elapsed time, or the shuffle seed, depending on Type.</summary>
public sealed record SimEvent(EventType Type, int Device = 0, long Value = 0, Fault Fault = Fault.None);

public sealed record Schedule(int Devices, long Total, ImmutableArray<SimEvent> Events)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static Schedule FromJson(string json) => JsonSerializer.Deserialize<Schedule>(json, Options)!;
}

/// <summary>A shrunk failing schedule, kept as a regression test.</summary>
public sealed record SavedRegression(string Mutant, string Violation, string Schedule);

/// <summary>
/// Every decision a mutant may change. The defaults are the real functions from Leeway.Core,
/// so a run with default parts tests the code that ships.
/// </summary>
public sealed record ProtocolParts
{
    public Func<MergeDocument, MergeDocument, MergeDocument> ServerMerge { get; init; } = (server, delta) => server.Merge(delta);

    public Func<MergeDocument, MergeDocument, MergeDocument> ViewMerge { get; init; } = (view, delta) => view.Merge(delta);

    public Func<HybridClock, long, string, HybridTimestamp> DeviceStamp { get; init; } = (clock, _, _) => clock.Now();

    public Func<StampAuthority, Operation, long, (HybridTimestamp Canonical, StampAuthority Next)> Admit { get; init; } = (authority, op, now) => authority.Admit(op, now);

    public Func<EscrowLedger, Operation, IAllocationPolicy, (EscrowLedger Ledger, OperationOutcome Outcome)> ApplyEscrow { get; init; } = EscrowRules.Apply;

    public Func<Wallet, long, Wallet?> WalletSpend { get; init; } = (wallet, amount) => wallet.TrySpend(amount);

    public Func<Wallet, long, (Wallet Wallet, long Amount)> WalletReturn { get; init; } = (wallet, amount) => wallet.Return(amount);

    public Func<long, long, long> NextRowStamp { get; init; } = (last, now) => Math.Max(now, last + 1);

    public bool ServerDedupes { get; init; } = true;

    public bool TrustsPushResult { get; init; }

    public bool SequenceSurvivesCrash { get; init; } = true;

    public bool ViewIncludesPending { get; init; } = true;
}

public sealed record SimStats
{
    public int SpendsConfirmed { get; init; }
    public int ProvisionalAccepted { get; init; }
    public int ProvisionalRejected { get; init; }
    public int Replays { get; init; }
    public int LeasesGranted { get; init; }
    public int Returns { get; init; }
    public int WriteOffs { get; init; }
    public int Overdrafts { get; init; }
    public int DriftCorrections { get; init; }
    public int RegistersWritten { get; init; }

    public SimStats Plus(SimStats o) => new()
    {
        SpendsConfirmed = SpendsConfirmed + o.SpendsConfirmed,
        ProvisionalAccepted = ProvisionalAccepted + o.ProvisionalAccepted,
        ProvisionalRejected = ProvisionalRejected + o.ProvisionalRejected,
        Replays = Replays + o.Replays,
        LeasesGranted = LeasesGranted + o.LeasesGranted,
        Returns = Returns + o.Returns,
        WriteOffs = WriteOffs + o.WriteOffs,
        Overdrafts = Overdrafts + o.Overdrafts,
        DriftCorrections = DriftCorrections + o.DriftCorrections,
        RegistersWritten = RegistersWritten + o.RegistersWritten,
    };
}

public sealed record SimResult(ImmutableArray<string> Violations, string TraceHash, SimStats Stats)
{
    public bool Passed => Violations.IsEmpty;
}
