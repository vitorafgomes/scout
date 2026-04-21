namespace Scout.Core.Tests;

public class SanityTests
{
    [Fact]
    public void Solution_Compiles_And_Test_Host_Runs()
    {
        var sut = 1 + 1;
        sut.Should().Be(2);
    }
}
