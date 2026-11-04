using Leeway.Core;

namespace Leeway.Core.Tests;

public class GuaranteeClassTests
{
    [Fact]
    public void There_are_exactly_three_guarantee_classes() =>
        Assert.Equal(3, Enum.GetValues<GuaranteeClass>().Length);
}
