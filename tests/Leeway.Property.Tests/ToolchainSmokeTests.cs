using FsCheck.Xunit;

namespace Leeway.Property.Tests;

/// <summary>Proves only that FsCheck runs under this xUnit. Merge-law properties arrive in M1.</summary>
public class ToolchainSmokeTests
{
    [Property]
    public bool FsCheck_generates_and_runs(int a, int b) => unchecked(a + b) == unchecked(b + a);
}
