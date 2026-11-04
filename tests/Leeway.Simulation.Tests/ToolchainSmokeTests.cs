namespace Leeway.Simulation.Tests;

/// <summary>Proves only that the project builds and is discovered. The simulator arrives in M2.</summary>
public class ToolchainSmokeTests
{
    [Fact]
    public void Simulation_assembly_loads() => Assert.NotNull(typeof(AssemblyMarker).Assembly);
}
