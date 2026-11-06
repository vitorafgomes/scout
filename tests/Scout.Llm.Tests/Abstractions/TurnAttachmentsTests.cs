using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests.Abstractions;

public sealed class TurnAttachmentsTests
{
    [Fact]
    public async Task A_Block_Attached_Outside_A_Turn_Is_Dropped_And_A_Turn_Never_Sees_A_Sibling_Turns_Block()
    {
        TurnAttachments.Attach("```outside\n{}\n```");
        TurnAttachments.Take().ShouldBeEmpty();

        var attached = 0;
        var bothAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<string>> TurnAsync(string block)
        {
            using var turn = TurnAttachments.Begin();
            await Task.Yield();
            TurnAttachments.Attach(block);
            if (Interlocked.Increment(ref attached) == 2)
            {
                bothAttached.SetResult();
            }

            await bothAttached.Task;
            return TurnAttachments.Take();
        }

        var first = Task.Run(() => TurnAsync("```first\n{}\n```"));
        var second = Task.Run(() => TurnAsync("```second\n{}\n```"));
        var taken = await Task.WhenAll(first, second);

        taken[0].ShouldBe(["```first\n{}\n```"]);
        taken[1].ShouldBe(["```second\n{}\n```"]);
        TurnAttachments.Take().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Block_Attached_In_A_Function_Of_The_Turn_Is_Taken_Once_At_The_End_Of_The_Turn()
    {
        using var turn = TurnAttachments.Begin();

        await Task.Run(async () =>
        {
            await Task.Yield();
            TurnAttachments.Attach("```scout-findings\n{}\n```");
        });

        TurnAttachments.Take().ShouldBe(["```scout-findings\n{}\n```"]);
        TurnAttachments.Take().ShouldBeEmpty();
    }

    [Fact]
    public void A_Turn_Keeps_Four_Blocks_Of_At_Most_64000_Characters_And_A_Block_With_The_Same_First_Line_Replaces_Its_Own()
    {
        using var turn = TurnAttachments.Begin();

        TurnAttachments.Attach("```a\n" + new string('x', TurnAttachments.MaxBlockLength));
        for (var i = 1; i <= 5; i++)
        {
            TurnAttachments.Attach($"```b{i}\n1\n```");
        }

        TurnAttachments.Attach("```b2\n2\n```");

        TurnAttachments.Take().ShouldBe(["```b1\n1\n```", "```b2\n2\n```", "```b3\n1\n```", "```b4\n1\n```"]);
    }
}
