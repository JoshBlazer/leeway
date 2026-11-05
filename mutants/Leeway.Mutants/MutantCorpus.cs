using System.Collections.Immutable;
using Leeway.Core;
using Leeway.Simulation;

namespace Leeway.Mutants;

/// <summary>A deliberately broken implementation.</summary>
/// <param name="Name">A short name.</param>
/// <param name="Mistake">The plausible mistake it models.</param>
/// <param name="Parts">The broken protocol.</param>
public sealed record Mutant(string Name, string Mistake, ProtocolParts Parts);

/// <summary>Measures how strong the simulator is. Each entry must be caught. See docs/design/simulation.md.</summary>
public static class MutantCorpus
{
    public static IReadOnlyList<Mutant> All { get; } =
    [
        new("MergeDropsIncrements", "A counter merge keeps the existing total for a replica instead of the maximum.",
            new ProtocolParts { ServerMerge = (server, delta) => WithCounters(server, delta, (a, b) => new PnCounter(First(a.Increments, b.Increments), First(a.Decrements, b.Decrements))) }),

        new("MergeSumsCounters", "A counter merge adds totals, so it is not idempotent.",
            new ProtocolParts { ServerMerge = (server, delta) => WithCounters(server, delta, (a, b) => new PnCounter(Sum(a.Increments, b.Increments), Sum(a.Decrements, b.Decrements))) }),

        new("WholeRowWins", "M0 finding C10: a field overwrite replaces the server's state, the way a resolver's forced write does.",
            new ProtocolParts { ServerMerge = (server, delta) => server with { Counters = server.Counters.SetItems(delta.Counters), Registers = server.Registers.SetItems(delta.Registers), Sets = server.Sets.SetItems(delta.Sets) } }),

        new("LwwFirstWriterWins", "The register comparison is backwards.",
            new ProtocolParts { ServerMerge = (server, delta) => { MergeDocument merged = server.Merge(delta); return merged with { Registers = server.Registers.SetItems(delta.Registers.Where(r => !server.Registers.ContainsKey(r.Key))) }; } }),

        new("LwwWithoutHlc", "Registers order by raw device wall clocks, so a slow device loses to what it has already seen.",
            new ProtocolParts
            {
                DeviceStamp = (_, wall, device) => new HybridTimestamp(wall, 0, device),
                Admit = (authority, op, _) => (op.Stamp, authority),
            }),

        new("AdmissionIgnoresObserved", "The server applies drift correction but forgets what the writer had seen.",
            new ProtocolParts
            {
                Admit = (authority, op, now) =>
                {
                    (HybridTimestamp canonical, StampAuthority next) = authority.Admit(op with { Observed = default }, now);
                    return (canonical, next);
                },
            }),

        new("DriftUnbounded", "No maximum drift: a device a day ahead outranks everyone for a day.",
            new ProtocolParts { Admit = (authority, op, _) => (new HybridTimestamp(Math.Max(op.Stamp.Physical, op.Observed.Physical + 1), (int)op.Sequence, op.Device), authority) }),

        new("SetRemoveWins", "A remove deletes every tag for the element, including adds it never saw.",
            new ProtocolParts
            {
                ServerMerge = (server, delta) =>
                {
                    MergeDocument merged = server.Merge(delta);
                    foreach ((string field, OrSet set) in delta.Sets)
                    {
                        OrSet current = merged.Set(field);
                        foreach ((string _, ImmutableSortedSet<string> tags) in current.Entries.Where(e => e.Value.Overlaps(set.Tombstones)))
                        {
                            current = current with { Tombstones = current.Tombstones.Union(tags) };
                        }

                        merged = merged.WithSet(field, current);
                    }

                    return merged;
                },
            }),

        new("ViewIgnoresPending", "The local view forgets unacknowledged operations, so a second offline add overwrites the first.",
            new ProtocolParts { ViewIncludesPending = false }),

        new("EscrowAllowsOverspend", "The wallet confirms a spend without checking the balance.",
            new ProtocolParts { WalletSpend = (wallet, amount) => wallet with { Spent = wallet.Spent + amount } }),

        new("WalletForgetsReturns", "A return is reported, but the wallet keeps the balance.",
            new ProtocolParts { WalletReturn = (wallet, amount) => (wallet, Math.Clamp(amount, 0, wallet.Available)) }),

        new("GrantIgnoresPool", "A lease is granted without checking what is unleased.",
            new ProtocolParts
            {
                ApplyEscrow = (ledger, op, policy) =>
                {
                    if (op.Kind != OperationKind.LeaseRequest)
                    {
                        return EscrowRules.Apply(ledger, op, policy);
                    }

                    EscrowLedger bigger = ledger with { Unleased = long.MaxValue / 2 };
                    (EscrowLedger updated, OperationOutcome outcome) = bigger.RequestLease(op.Device, op.Amount, policy);
                    return (updated with { Unleased = ledger.Unleased - outcome.Amount }, outcome);
                },
            }),

        new("ProvisionalIgnoresPool", "A provisional spend is accepted without checking what is unleased.",
            new ProtocolParts
            {
                ApplyEscrow = (ledger, op, policy) =>
                {
                    if (op.Kind != OperationKind.ProvisionalSpend)
                    {
                        return EscrowRules.Apply(ledger, op, policy);
                    }

                    (EscrowLedger updated, OperationOutcome outcome) = (ledger with { Unleased = ledger.Unleased + op.Amount }).SpendProvisional(op.Device, op.Amount);
                    return (updated with { Unleased = ledger.Unleased - op.Amount }, outcome);
                },
            }),

        new("ReturnNotCredited", "A returned lease never goes back in the pool.",
            new ProtocolParts
            {
                ApplyEscrow = (ledger, op, policy) =>
                {
                    (EscrowLedger updated, OperationOutcome outcome) = EscrowRules.Apply(ledger, op, policy);
                    return op.Kind == OperationKind.ReturnLease ? (updated with { Unleased = ledger.Unleased }, outcome) : (updated, outcome);
                },
            }),

        new("NoDeduplication", "The server applies an operation every time it arrives.",
            new ProtocolParts { ServerDedupes = false }),

        new("TrustsPushResult", "M0 finding C24: a client believes PushResult.IsSuccessful and drops timed-out operations.",
            new ProtocolParts { TrustsPushResult = true }),

        new("NonMonotonicRowStamps", "M0 finding C19: rows are stamped with the plain clock, so a same-millisecond update is never pulled.",
            new ProtocolParts { NextRowStamp = (_, now) => now }),

        new("OperationIdsReusedAfterCrash", "The sequence number is not durable, so a restart reuses IDs, and the replay check swallows real work.",
            new ProtocolParts { SequenceSurvivesCrash = false }),
    ];

    private static MergeDocument WithCounters(MergeDocument server, MergeDocument delta, Func<PnCounter, PnCounter, PnCounter> merge)
    {
        MergeDocument merged = server.Merge(delta);
        foreach ((string field, PnCounter counter) in delta.Counters)
        {
            merged = merged.WithCounter(field, server.Counters.TryGetValue(field, out PnCounter? existing) ? merge(existing, counter) : counter);
        }

        return merged;
    }

    private static ImmutableSortedDictionary<string, long> First(ImmutableSortedDictionary<string, long> a, ImmutableSortedDictionary<string, long> b)
        => b.Aggregate(a, (map, item) => map.ContainsKey(item.Key) ? map : map.SetItem(item.Key, item.Value));

    private static ImmutableSortedDictionary<string, long> Sum(ImmutableSortedDictionary<string, long> a, ImmutableSortedDictionary<string, long> b)
        => b.Aggregate(a, (map, item) => map.SetItem(item.Key, map.GetValueOrDefault(item.Key) + item.Value));
}

/// <summary>A mutant caught at a seed.</summary>
public sealed record Catch(int Seed, Schedule Schedule, string Violation);

public static class MutantHunter
{
    /// <summary>Shrinks a catch, keeping the schedule valid: it must still fail on the mutant and pass on the correct code.</summary>
    public static Schedule Shrink(Mutant mutant, Schedule failing)
        => Shrinker.Shrink(failing, s => !Simulator.Run(s, mutant.Parts).Passed && Simulator.Run(s).Passed);

    /// <summary>
    /// Tries seeds until the mutant breaks a property on a schedule the correct code passes.
    /// Without that second condition, a bug in the correct code would look like a 100% catch rate.
    /// </summary>
    /// <returns>The catch, or null if the mutant survived every seed.</returns>
    public static Catch? Hunt(Mutant mutant, int maxSeeds)
    {
        for (int seed = 1; seed <= maxSeeds; seed++)
        {
            Schedule schedule = ScheduleGenerator.Generate(seed);
            SimResult result = Simulator.Run(schedule, mutant.Parts);
            if (!result.Passed && Simulator.Run(schedule).Passed)
            {
                return new Catch(seed, schedule, result.Violations[0]);
            }
        }

        return null;
    }
}
