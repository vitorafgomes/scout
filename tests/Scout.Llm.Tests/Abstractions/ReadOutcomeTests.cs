using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests.Abstractions;

public sealed class ReadOutcomeTests
{
    [Fact]
    public async Task A_Mark_Made_In_The_Function_Of_The_Call_Reaches_The_Scope_Around_That_Call()
    {
        using var call = ReadOutcome.Begin();
        var before = call.NoContent;

        await Task.Run(async () =>
        {
            await Task.Yield();
            ReadOutcome.MarkNoContent();
        });

        before.ShouldBeFalse();
        call.NoContent.ShouldBeTrue();
    }

    [Fact]
    public async Task Of_Two_Calls_At_Once_Only_The_One_That_Marked_Is_Marked()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> CallAsync(bool fails)
        {
            using var call = ReadOutcome.Begin();
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.SetResult();
            }

            await bothEntered.Task;
            if (fails)
            {
                ReadOutcome.MarkNoContent();
            }

            await Task.Yield();
            return call.NoContent;
        }

        var failing = CallAsync(fails: true);
        var reading = CallAsync(fails: false);

        (await failing).ShouldBeTrue();
        (await reading).ShouldBeFalse();
    }

    [Fact]
    public void A_Mark_Never_Reaches_The_Call_After_It_The_Call_Around_It_Or_A_Caller_Outside_Any_Scope()
    {
        ReadOutcome.MarkNoContent();
        using var outer = ReadOutcome.Begin();
        using (var first = ReadOutcome.Begin())
        {
            ReadOutcome.MarkNoContent();
            first.NoContent.ShouldBeTrue();
        }

        using (var next = ReadOutcome.Begin())
        {
            next.NoContent.ShouldBeFalse();
        }

        outer.NoContent.ShouldBeFalse();
        ReadOutcome.MarkNoContent();
        outer.NoContent.ShouldBeTrue();
    }
}
