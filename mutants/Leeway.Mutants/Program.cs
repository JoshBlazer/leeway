using System.Diagnostics;
using System.Text.Json;
using Leeway.Mutants;
using Leeway.Simulation;

// Commands:
//   catch [--save <dir>]            Reports the mutant catch rate, optionally saving shrunk schedules as regressions.
//   soak <runs> <startSeed> [<dir>] Runs seeds on the correct implementation. Failures are shrunk and saved to <dir>.
string command = args.Length > 0 ? args[0] : "catch";
return command switch
{
    "catch" => Catch(args.Length > 2 && args[1] == "--save" ? args[2] : null),
    "soak" => Soak(int.Parse(args[1]), int.Parse(args[2]), args.Length > 3 ? args[3] : "simulation-failures"),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: catch [--save <dir>] | soak <runs> <startSeed> [<failureDir>]");
    return 2;
}

static int Catch(string? saveDirectory)
{
    const int MaxSeeds = 3_000;
    int caught = 0;
    Console.WriteLine($"{"Mutant",-30} {"Seed",6} {"Events",14}  First violation");
    foreach (Mutant mutant in MutantCorpus.All)
    {
        Catch? found = MutantHunter.Hunt(mutant, MaxSeeds);
        if (found is null)
        {
            Console.WriteLine($"{mutant.Name,-30} SURVIVED {MaxSeeds} seeds");
            continue;
        }

        caught++;
        Schedule shrunk = MutantHunter.Shrink(mutant, found.Schedule);
        string violation = Simulator.Run(shrunk, mutant.Parts).Violations[0];
        Console.WriteLine($"{mutant.Name,-30} {found.Seed,6} {found.Schedule.Events.Length,5} -> {shrunk.Events.Length,4}  {violation}");
        if (saveDirectory is not null)
        {
            Directory.CreateDirectory(saveDirectory);
            string json = JsonSerializer.Serialize(new SavedRegression(mutant.Name, violation, shrunk.ToJson()), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(saveDirectory, mutant.Name + ".json"), json);
        }
    }

    double rate = 100.0 * caught / MutantCorpus.All.Count;
    Console.WriteLine($"Catch rate: {caught} of {MutantCorpus.All.Count} ({rate:F0}%), searching up to {MaxSeeds} seeds each.");
    return caught == MutantCorpus.All.Count ? 0 : 1;
}

static int Soak(int runs, int startSeed, string failureDirectory)
{
    Stopwatch watch = Stopwatch.StartNew();
    SimStats totals = new();
    int failures = 0;
    for (int i = 0; i < runs; i++)
    {
        int seed = unchecked(startSeed + i);
        Schedule schedule = ScheduleGenerator.Generate(seed);
        SimResult result = Simulator.Run(schedule);
        totals = totals.Plus(result.Stats);
        if (result.Passed)
        {
            continue;
        }

        failures++;
        Schedule shrunk = Shrinker.Shrink(schedule, s => !Simulator.Run(s).Passed);
        string violation = Simulator.Run(shrunk).Violations[0];
        Console.WriteLine($"FAILED seed {seed}: {violation}");
        Directory.CreateDirectory(failureDirectory);
        File.WriteAllText(Path.Combine(failureDirectory, $"seed-{seed}.json"), JsonSerializer.Serialize(new SavedRegression("(correct implementation)", violation, shrunk.ToJson())));
    }

    Console.WriteLine($"Runs: {runs}, start seed: {startSeed}, failures: {failures}, time: {watch.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine(totals);
    return failures == 0 ? 0 : 1;
}
