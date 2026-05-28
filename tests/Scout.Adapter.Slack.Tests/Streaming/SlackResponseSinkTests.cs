using Scout.Adapter.Slack.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Slack.Tests.Streaming;

public sealed class SlackResponseSinkTests
{
    private const string Cursor = "▌";
    private static readonly SlackMessageHandle Handle = new("C1", "1700000000.000100");

    private static (SlackResponseSink Sink, List<string> Posts, List<string> Edits) Create(
        Func<SlackMessageHandle, string, CancellationToken, Task>? editOverride = null)
    {
        var posts = new List<string>();
        var edits = new List<string>();
        Func<string, CancellationToken, Task<SlackMessageHandle>> post = (body, _) =>
        {
            posts.Add(body);
            return Task.FromResult(Handle);
        };
        Func<SlackMessageHandle, string, CancellationToken, Task> edit = editOverride ?? ((_, body, _) =>
        {
            edits.Add(body);
            return Task.CompletedTask;
        });
        return (new SlackResponseSink(post, edit), posts, edits);
    }

    [Fact]
    public void Capabilities_Are_FineGrained_3000_With_One_Second_Floor()
    {
        var (sink, _, _) = Create();
        sink.Capabilities.FineGrainedEdit.ShouldBeTrue();
        sink.Capabilities.MaxBodyLength.ShouldBe(3000);
        sink.Capabilities.SupportsInterimCitations.ShouldBeFalse();
        sink.Capabilities.EditIntervalFloor.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Begin_Posts_Placeholder_Seeded_With_Cursor()
    {
        var (sink, posts, _) = Create();

        await sink.BeginAsync(CancellationToken.None);

        posts.ShouldHaveSingleItem();
        posts[0].ShouldBe(Cursor);
    }

    [Fact]
    public async Task Update_Edits_With_Cumulative_Plus_Cursor()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await sink.UpdateAsync("hello", CancellationToken.None);
        await sink.UpdateAsync("hello world", CancellationToken.None);

        edits.ShouldBe(["hello" + Cursor, "hello world" + Cursor]);
    }

    [Fact]
    public async Task Complete_Edits_Without_Cursor_And_Appends_Citations()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await sink.CompleteAsync(
            "final answer",
            new[] { new Citation("src/A.cs:1") },
            CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldNotContain(Cursor);
        edits[0].ShouldStartWith("final answer");
        edits[0].ShouldContain("Sources:");
        edits[0].ShouldContain("- src/A.cs:1");
    }

    [Fact]
    public async Task Complete_Without_Citations_Edits_Plain_Final()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("done");
    }

    [Fact]
    public async Task Fail_Appends_Error_Footer_To_Partial()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await sink.FailAsync("partial answer", "boom", CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldStartWith("partial answer");
        edits[0].ShouldEndWith("(connection error)");
    }

    [Fact]
    public async Task Fail_With_Empty_Partial_Renders_Only_Error()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await sink.FailAsync(string.Empty, "boom", CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].ShouldBe("(connection error)");
    }

    [Fact]
    public async Task Complete_OverLimit_Body_Preserves_Head_Not_Just_Footer()
    {
        const int max = 3000;
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        // An answer that already fills the budget, plus citations that would push
        // it over. The head of the answer must survive; the user must not be left
        // with only the Sources footer.
        var answer = "HEAD-OF-ANSWER " + new string('a', max);
        await sink.CompleteAsync(
            answer,
            new[] { new Citation("src/A.cs:1") },
            CancellationToken.None);

        var body = edits[^1];
        body.Length.ShouldBeLessThanOrEqualTo(max);
        body.ShouldStartWith("HEAD-OF-ANSWER ");
        body.ShouldEndWith("... (truncated)\n\n");
        body.ShouldNotContain("Sources:");
    }

    [Fact]
    public async Task Update_OverLimit_Body_Is_Truncated_With_Cursor()
    {
        var (sink, _, edits) = Create();
        await sink.BeginAsync(CancellationToken.None);

        var huge = new string('x', 5000);
        await sink.UpdateAsync(huge, CancellationToken.None);

        edits.ShouldHaveSingleItem();
        edits[0].Length.ShouldBeLessThanOrEqualTo(3000);
        edits[0].ShouldEndWith(Cursor);
    }

    [Fact]
    public async Task Lifecycle_Rejects_Update_Before_Begin()
    {
        var (sink, _, _) = Create();

        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.UpdateAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Update_After_Terminal()
    {
        var (sink, _, _) = Create();
        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.UpdateAsync("more", CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Second_Terminal()
    {
        var (sink, _, _) = Create();
        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.FailAsync("x", "y", CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_Rejects_Double_Begin()
    {
        var (sink, _, _) = Create();
        await sink.BeginAsync(CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.BeginAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Edits_Target_The_Channel_And_Ts_Captured_At_Begin()
    {
        SlackMessageHandle? seen = null;
        var (sink, _, _) = Create(editOverride: (handle, _, _) =>
        {
            seen = handle;
            return Task.CompletedTask;
        });

        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("done", Array.Empty<Citation>(), CancellationToken.None);

        seen.ShouldBe(Handle);
    }
}
