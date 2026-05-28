using Scout.Adapter.Discord.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Tests.Streaming;

public sealed class DiscordResponseSinkTests
{
    private const string Cursor = "▌";

    private static (DiscordResponseSink Sink, List<string> Posts, List<string> Edits) Create(
        Func<string, CancellationToken, Task>? editOverride = null)
    {
        var posts = new List<string>();
        var edits = new List<string>();
        Func<string, CancellationToken, Task> post = (body, _) =>
        {
            posts.Add(body);
            return Task.CompletedTask;
        };
        Func<string, CancellationToken, Task> edit = editOverride ?? ((body, _) =>
        {
            edits.Add(body);
            return Task.CompletedTask;
        });
        return (new DiscordResponseSink(post, edit), posts, edits);
    }

    [Fact]
    public void Capabilities_Are_FineGrained_4096()
    {
        var (sink, _, _) = Create();
        sink.Capabilities.FineGrainedEdit.ShouldBeTrue();
        sink.Capabilities.MaxBodyLength.ShouldBe(4096);
        sink.Capabilities.SupportsInterimCitations.ShouldBeFalse();
    }

    [Fact]
    public async Task Begin_Posts_Embed_Seeded_With_Cursor()
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
}
