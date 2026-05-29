using Scout.Host.ControlPlane.Security;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Security;

public sealed class SessionTokenTests
{
    [Fact]
    public void Matches_ExactValue_ReturnsTrue()
    {
        var token = SessionToken.FromValue("abc123");
        token.Matches("abc123").ShouldBeTrue();
    }

    [Theory]
    [InlineData("abc124")]
    [InlineData("abc12")]
    [InlineData("abc1234")]
    [InlineData("")]
    [InlineData(null)]
    public void Matches_WrongValue_ReturnsFalse(string? presented)
    {
        var token = SessionToken.FromValue("abc123");
        token.Matches(presented).ShouldBeFalse();
    }

    [Fact]
    public void FromEnvironmentOrMint_NoEnv_MintsNonEmptyToken()
    {
        Environment.SetEnvironmentVariable(SessionToken.EnvVarName, null);
        var token = SessionToken.FromEnvironmentOrMint();
        token.Value.ShouldNotBeNullOrWhiteSpace();
        token.Value.Length.ShouldBeGreaterThanOrEqualTo(32);
    }

    [Fact]
    public void FromEnvironmentOrMint_WithEnv_UsesEnvValue()
    {
        try
        {
            Environment.SetEnvironmentVariable(SessionToken.EnvVarName, "from-env");
            var token = SessionToken.FromEnvironmentOrMint();
            token.Value.ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionToken.EnvVarName, null);
        }
    }
}
