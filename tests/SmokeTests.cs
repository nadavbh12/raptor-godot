using Xunit;

namespace Raptor.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProjectCompiles()
    {
        Assert.Equal(2, 1 + 1);
    }
}
