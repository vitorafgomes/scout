using Discord;
using Scout.Adapter.Discord.Streaming;

namespace Scout.Adapter.Discord.Tests.Streaming;

public sealed class DiscordResponseSinkPartsTests
{
    private const string Cursor = "▌";

    private sealed class Transport
    {
        public List<(int Index, string Body)> Posts { get; } = [];
        public List<(int Index, string Body)> Edits { get; } = [];
        public int TypingStarted { get; private set; }
        public int TypingStopped { get; private set; }
        public Func<int, Exception?> FailPost { get; set; } = _ => null;

        public DiscordResponseSink Sink() => new(
            post: (index, body, _) =>
            {
                if (FailPost(index) is { } failure)
                {
                    throw failure;
                }
                Posts.Add((index, body));
                return Task.CompletedTask;
            },
            edit: (index, body, _) =>
            {
                Edits.Add((index, body));
                return Task.CompletedTask;
            },
            enterTyping: () =>
            {
                TypingStarted++;
                return new Stop(() => TypingStopped++);
            });

        private sealed class Stop(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private static string Paragraph(char c) => new(c, 3000);

    [Fact]
    public async Task A_Reply_Longer_Than_One_Embed_Continues_In_Follow_Up_Messages_In_Order()
    {
        var transport = new Transport();
        var sink = transport.Sink();
        await sink.BeginAsync(CancellationToken.None);

        await sink.CompleteAsync(
            Paragraph('a') + "\n\n" + Paragraph('b') + "\n\n" + new string('c', 100), [], CancellationToken.None);

        transport.Edits.ShouldBe([(0, Paragraph('a'))]);
        transport.Posts.ShouldBe([(0, Cursor), (1, Paragraph('b') + "\n\n" + new string('c', 100))]);
        transport.Posts.ShouldAllBe(p => p.Body.Length <= DiscordResponseSink.MaxEmbedDescriptionLength);
    }

    [Fact]
    public async Task A_Reply_Over_Five_Embeds_Stops_At_Five_With_Reply_Truncated()
    {
        var transport = new Transport();
        var sink = transport.Sink();
        await sink.BeginAsync(CancellationToken.None);
        var text = string.Join("\n\n", "abcdefg".Select(Paragraph));

        await sink.CompleteAsync(text, [], CancellationToken.None);

        transport.Posts.Select(p => p.Index).ShouldBe([0, 1, 2, 3, 4]);
        transport.Posts[^1].Body.ShouldBe(Paragraph('e') + "\n\n(reply truncated)");
        transport.Posts.ShouldNotContain(p => p.Body.Contains('f'));
    }

    [Fact]
    public async Task Streaming_Past_One_Embed_Posts_The_Next_Part_Without_Rewriting_The_Sealed_One()
    {
        var transport = new Transport();
        var sink = transport.Sink();
        await sink.BeginAsync(CancellationToken.None);

        await sink.UpdateAsync(Paragraph('a') + "\n\n" + Paragraph('b') + Cursor, CancellationToken.None);
        await sink.UpdateAsync(Paragraph('a') + "\n\n" + Paragraph('b') + " more" + Cursor, CancellationToken.None);

        transport.Edits.ShouldBe([(0, Paragraph('a')), (1, Paragraph('b') + " more" + Cursor)]);
        transport.Posts.ShouldBe([(0, Cursor), (1, Paragraph('b') + Cursor)]);
    }

    [Fact]
    public async Task A_Follow_Up_Post_That_Fails_Marks_The_Reply_Incomplete_And_Does_Not_Throw()
    {
        var transport = new Transport { FailPost = i => i == 1 ? new HttpRequestException("Discord returned 500") : null };
        var sink = transport.Sink();
        await sink.BeginAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => sink.CompleteAsync(
            Paragraph('a') + "\n\n" + Paragraph('b'), [], CancellationToken.None));

        transport.Edits.ShouldBe([
            (0, Paragraph('a')),
            (0, Paragraph('a') + "\n\n(the rest of this reply could not be posted; see Scout logs)"),
        ]);
        transport.Posts.ShouldBe([(0, Cursor)]);
    }

    [Fact]
    public async Task Typing_Starts_At_Begin_And_Stops_At_The_First_Streamed_Content()
    {
        var transport = new Transport();
        var sink = transport.Sink();

        await sink.BeginAsync(CancellationToken.None);
        transport.TypingStarted.ShouldBe(1);

        await sink.UpdateAsync(Cursor, CancellationToken.None);
        transport.TypingStopped.ShouldBe(0, "no content has streamed yet");

        await sink.UpdateAsync("The build" + Cursor, CancellationToken.None);
        transport.TypingStopped.ShouldBe(1);

        await sink.CompleteAsync("The build is green.", [], CancellationToken.None);
        transport.TypingStopped.ShouldBe(1);
    }

    [Fact]
    public async Task Typing_Stops_On_Complete_And_On_Fail_Without_Content()
    {
        var completed = new Transport();
        var sink = completed.Sink();
        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("Scout only answers its configured users.", [], CancellationToken.None);

        var failed = new Transport();
        var failing = failed.Sink();
        await failing.BeginAsync(CancellationToken.None);
        await failing.FailAsync(string.Empty, "boom", CancellationToken.None);

        completed.TypingStopped.ShouldBe(1);
        failed.TypingStopped.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_A_Reply_Cancelled_Before_Its_Terminal_Call_Stops_Typing()
    {
        var transport = new Transport();
        var sink = transport.Sink();
        await sink.BeginAsync(CancellationToken.None);

        sink.Dispose();

        transport.TypingStopped.ShouldBe(1);
    }

    [Fact]
    public async Task The_First_Reply_Message_References_The_Users_Message_And_Follow_Ups_Do_Not()
    {
        var channel = Substitute.For<IMessageChannel>();
        var references = new List<MessageReference?>();
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                references.Add(call.ArgAt<MessageReference?>(5));
                return Substitute.For<IUserMessage>();
            });
        var sink = new DiscordResponseSink(channel, new MessageReference(42, 3333, 9999, failIfNotExists: false));

        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync(Paragraph('a') + "\n\n" + Paragraph('b'), [], CancellationToken.None);

        references.Count.ShouldBe(2);
        references[0].ShouldNotBeNull();
        references[0]!.MessageId.Value.ShouldBe(42UL);
        references[0]!.FailIfNotExists.Value.ShouldBeFalse();
        references[1].ShouldBeNull();
    }

    [Fact]
    public async Task The_Production_Sink_Enters_The_Channels_Typing_State_And_Leaves_It_On_Content()
    {
        var channel = Substitute.For<IMessageChannel>();
        var typing = Substitute.For<IDisposable>();
        channel.EnterTypingState(Arg.Any<RequestOptions>()).Returns(typing);
        var sink = new DiscordResponseSink(channel);

        await sink.BeginAsync(CancellationToken.None);
        typing.DidNotReceive().Dispose();

        await sink.UpdateAsync("hi" + Cursor, CancellationToken.None);
        typing.Received(1).Dispose();
    }

    [Fact]
    public async Task A_Typing_Indicator_Discord_Refuses_Never_Stops_The_Answer()
    {
        var channel = Substitute.For<IMessageChannel>();
        channel.EnterTypingState(Arg.Any<RequestOptions>())
            .Returns(_ => throw new HttpRequestException("Discord returned 500"));
        var message = Substitute.For<IUserMessage>();
        var bodies = new List<string>();
        channel.SendMessageAsync(default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                bodies.Add(call.ArgAt<Embed>(2).Description);
                return message;
            });
        message.ModifyAsync(Arg.Do<Action<MessageProperties>>(change =>
        {
            var props = new MessageProperties();
            change(props);
            bodies.Add(props.Embed.Value.Description);
        }), Arg.Any<RequestOptions>()).Returns(Task.CompletedTask);
        var sink = new DiscordResponseSink(channel);

        await sink.BeginAsync(CancellationToken.None);
        await sink.CompleteAsync("The build is green.", [], CancellationToken.None);

        bodies.ShouldBe([Cursor, "The build is green."]);
    }
}
