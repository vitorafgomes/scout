using Scout.Contracts;

namespace Scout.Contracts.Tests;

/// <summary>
/// The AsyncLocal context is the load-bearing mechanism for identity plumbing:
/// it must be null by default, restore the previous value on scope disposal
/// (including nested scopes in stack order), flow into an awaited callee, never
/// leak a value set inside a callee back up, and keep concurrent flows isolated.
/// If any of these break, output gating silently fails open or closed.
/// </summary>
public sealed class AsyncLocalScoutRequestContextTests
{
    private static ScoutRequestIdentity Id(string user) =>
        new(user, "c1", IsDirectMessage: false, AdapterName: "Test");

    [Fact]
    public void Current_Is_Null_By_Default()
    {
        var context = new AsyncLocalScoutRequestContext();

        context.Current.ShouldBeNull();
    }

    [Fact]
    public void BeginScope_Sets_Current_And_Dispose_Restores_To_Null()
    {
        var context = new AsyncLocalScoutRequestContext();
        var identity = Id("u1");

        using (context.BeginScope(identity))
        {
            context.Current.ShouldBeSameAs(identity);
        }

        context.Current.ShouldBeNull();
    }

    [Fact]
    public void Nested_Scopes_Restore_In_Stack_Order()
    {
        var context = new AsyncLocalScoutRequestContext();
        var outer = Id("outer");
        var inner = Id("inner");

        using (context.BeginScope(outer))
        {
            context.Current.ShouldBeSameAs(outer);

            using (context.BeginScope(inner))
            {
                context.Current.ShouldBeSameAs(inner);
            }

            // Inner disposed: the outer identity is restored, not null.
            context.Current.ShouldBeSameAs(outer);
        }

        context.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Value_Set_Before_An_Awaited_Callee_Is_Visible_Inside_It()
    {
        var context = new AsyncLocalScoutRequestContext();
        var identity = Id("u1");

        ScoutRequestIdentity? observed = null;

        async Task Callee()
        {
            // Force real asynchrony so the value must flow over a continuation.
            await Task.Yield();
            observed = context.Current;
        }

        using (context.BeginScope(identity))
        {
            await Callee();
        }

        observed.ShouldBeSameAs(identity);
    }

    [Fact]
    public async Task Value_Set_Inside_A_Callee_Does_Not_Leak_Up()
    {
        var context = new AsyncLocalScoutRequestContext();

        async Task Callee()
        {
            await Task.Yield();
            using (context.BeginScope(Id("inner")))
            {
                context.Current.ShouldNotBeNull();
            }
        }

        await Callee();

        // A scope opened deep in a callee must not survive back into the caller.
        context.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Two_Concurrent_Flows_With_Own_Scopes_Never_See_Each_Other()
    {
        var context = new AsyncLocalScoutRequestContext();
        var barrier = new TaskCompletionSource();

        async Task<string?> Flow(string user)
        {
            using (context.BeginScope(Id(user)))
            {
                // Rendezvous so both flows are inside their scope simultaneously,
                // then re-read after resuming to prove no cross-talk.
                if (user == "a")
                {
                    barrier.SetResult();
                }

                await barrier.Task;
                await Task.Yield();
                return context.Current?.UserId;
            }
        }

        var a = Flow("a");
        var b = Flow("b");
        var results = await Task.WhenAll(a, b);

        results[0].ShouldBe("a");
        results[1].ShouldBe("b");
        context.Current.ShouldBeNull();
    }

    [Fact]
    public void BeginScope_Throws_When_Identity_Null()
    {
        var context = new AsyncLocalScoutRequestContext();

        Should.Throw<ArgumentNullException>(() => context.BeginScope(null!));
    }

    [Fact]
    public void Double_Dispose_Is_Harmless()
    {
        var context = new AsyncLocalScoutRequestContext();
        var scope = context.BeginScope(Id("u1"));

        scope.Dispose();
        Should.NotThrow(scope.Dispose);
        context.Current.ShouldBeNull();
    }
}
