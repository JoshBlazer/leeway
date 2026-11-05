using System.Collections.Immutable;
using System.Text.Json;
using Leeway.Mutants;

namespace Leeway.Simulation.Tests;

public class SimulatorTests
{
    /// <summary>M2 exit check, second half.</summary>
    [Fact]
    public void Ten_thousand_seeded_runs_are_green_on_the_correct_implementation()
    {
        for (int seed = 1; seed <= 10_000; seed++)
        {
            SimResult result = Simulator.Run(ScheduleGenerator.Generate(seed));
            Assert.True(result.Passed, $"Seed {seed}: {string.Join("; ", result.Violations)}");
        }
    }

    /// <summary>M2 exit check, first half: the catch rate.</summary>
    [Fact]
    public void The_simulator_catches_every_mutant()
    {
        List<string> missed = [];
        foreach (Mutant mutant in MutantCorpus.All)
        {
            if (MutantHunter.Hunt(mutant, maxSeeds: 3_000) is null)
            {
                missed.Add(mutant.Name);
            }
        }

        Assert.Empty(missed);
    }

    [Fact]
    public void The_corpus_includes_the_mutants_the_brief_names()
    {
        string[] names = MutantCorpus.All.Select(m => m.Name).ToArray();
        Assert.Contains("MergeDropsIncrements", names);
        Assert.Contains("EscrowAllowsOverspend", names);
        Assert.Contains("LwwWithoutHlc", names);
    }

    [Fact]
    public void A_run_is_a_pure_function_of_its_schedule()
    {
        for (int seed = 1; seed <= 200; seed++)
        {
            Schedule schedule = ScheduleGenerator.Generate(seed);
            Assert.Equal(Simulator.Run(schedule).TraceHash, Simulator.Run(schedule).TraceHash);
            Assert.Equal(schedule.ToJson(), ScheduleGenerator.Generate(seed).ToJson());
        }
    }

    [Fact]
    public void Different_seeds_explore_different_runs()
    {
        int distinct = Enumerable.Range(1, 200).Select(seed => Simulator.Run(ScheduleGenerator.Generate(seed)).TraceHash).Distinct().Count();
        Assert.True(distinct > 190, $"Only {distinct} distinct traces in 200 seeds.");
    }

    /// <summary>A simulator that never exercises the protocol would pass everything, so check that it does.</summary>
    [Fact]
    public void The_generated_schedules_really_exercise_the_protocol()
    {
        SimStats total = new();
        for (int seed = 1; seed <= 500; seed++)
        {
            total = total.Plus(Simulator.Run(ScheduleGenerator.Generate(seed)).Stats);
        }

        Assert.True(total.SpendsConfirmed > 500);
        Assert.True(total.ProvisionalAccepted > 50);
        Assert.True(total.ProvisionalRejected > 50);
        Assert.True(total.Replays > 200);
        Assert.True(total.LeasesGranted > 500);
        Assert.True(total.Returns > 100);
        Assert.True(total.WriteOffs > 50);
        Assert.True(total.Overdrafts > 0);
        Assert.True(total.DriftCorrections > 50);
        Assert.True(total.RegistersWritten > 500);
    }

    [Fact]
    public void A_shrunk_schedule_still_fails_and_is_one_minimal()
    {
        Mutant mutant = MutantCorpus.All.Single(m => m.Name == "TrustsPushResult");
        Schedule failing = MutantHunter.Hunt(mutant, 3_000)!.Schedule;

        Schedule shrunk = MutantHunter.Shrink(mutant, failing);

        Assert.False(Simulator.Run(shrunk, mutant.Parts).Passed);
        Assert.True(shrunk.Events.Length < failing.Events.Length);
        for (int i = 0; i < shrunk.Events.Length; i++)
        {
            Schedule without = shrunk with { Events = shrunk.Events.RemoveAt(i) };
            Assert.True(Simulator.Run(without, mutant.Parts).Passed || !Simulator.Run(without).Passed, $"Removing event {i} still catches the mutant, so it is not 1-minimal.");
        }
    }

    [Fact]
    public void Schedules_survive_a_json_round_trip()
    {
        Schedule schedule = ScheduleGenerator.Generate(7);
        Assert.Equal(schedule.ToJson(), Schedule.FromJson(schedule.ToJson()).ToJson());
    }

    /// <summary>Saved regressions: each must still catch its mutant and pass on the correct code.</summary>
    [Fact]
    public void Every_saved_regression_replays()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "regressions");
        string[] files = Directory.GetFiles(directory, "*.json");
        Assert.Equal(MutantCorpus.All.Count, files.Length);

        foreach (string file in files)
        {
            SavedRegression saved = JsonSerializer.Deserialize<SavedRegression>(File.ReadAllText(file))!;
            Mutant mutant = MutantCorpus.All.Single(m => m.Name == saved.Mutant);
            Schedule schedule = Schedule.FromJson(saved.Schedule);

            SimResult correct = Simulator.Run(schedule);
            Assert.True(correct.Passed, $"{file}: {string.Join("; ", correct.Violations)}");
            Assert.False(Simulator.Run(schedule, mutant.Parts).Passed, $"{file} no longer catches {saved.Mutant}.");
        }
    }

    [Fact]
    public void A_write_that_saw_another_wins_even_from_a_device_with_a_slow_clock()
    {
        Schedule schedule = new(2, 10, [
            new SimEvent(EventType.Advance, Value: 1_000_000),
            new SimEvent(EventType.RegisterWrite, Device: 0),
            new SimEvent(EventType.Push, Device: 0),
            new SimEvent(EventType.Skew, Device: 1, Value: -500_000),
            new SimEvent(EventType.Pull, Device: 1),
            new SimEvent(EventType.RegisterWrite, Device: 1),
        ]);

        Assert.True(Simulator.Run(schedule).Passed);
        Assert.False(Simulator.Run(schedule, MutantCorpus.All.Single(m => m.Name == "LwwWithoutHlc").Parts).Passed);
    }
}
