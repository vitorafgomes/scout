using Scout.Contracts;

namespace Scout.Contracts.Tests;

/// <summary>
/// The null context is the fail-closed default for hosts/tests that never set an
/// identity: Current is always null and BeginScope is an accepted no-op.
/// </summary>
public sealed class NullScoutRequestContextTests
{
    [Fact]
    public void Current_Is_Always_Null()
    {
        NullScoutRequestContext.Instance.Current.ShouldBeNull();
    }

    [Fact]
    public void BeginScope_Is_A_Noop_And_Current_Stays_Null()
    {
        var context = NullScoutRequestContext.Instance;

        using (context.BeginScope(new ScoutRequestIdentity("u1", "c1", true, "Test")))
        {
            context.Current.ShouldBeNull();
        }

        context.Current.ShouldBeNull();
    }

    [Fact]
    public void BeginScope_Throws_When_Identity_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => NullScoutRequestContext.Instance.BeginScope(null!));
    }
}
