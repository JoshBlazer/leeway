# Mutants

Deliberately broken implementations that measure how strong the simulator is. `Leeway.Mutants/MutantCorpus.cs` lists them, with the plausible mistake each one models. They include each trap found in M0, and unsafe retention.

```
dotnet run --project mutants/Leeway.Mutants -c Release -- catch
dotnet run --project mutants/Leeway.Mutants -c Release -- catch --save tests/Leeway.Simulation.Tests/regressions
dotnet run --project mutants/Leeway.Mutants -c Release -- soak 10000 1
```

A mutant only counts as caught on a schedule the correct implementation passes. See `docs/design/simulation.md`.
