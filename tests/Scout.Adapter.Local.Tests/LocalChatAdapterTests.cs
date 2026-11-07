using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;

namespace Scout.Adapter.Local.Tests;

public sealed class LocalChatAdapterTests
{
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _reader;

        public Harness(
            Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
            TextWriter? stdout = null,
            ILiveTranscriber? live = null,
            ILogger<LocalChatAdapter>? logger = null, IScreenTextReader? reader = null,
            ILiveTranslator? translator = null,
            Func<CancellationToken, Task<string?>>? mandateLanguage = null,
            IMeetingStore? meetings = null,
            Func<MeetingRecord, string>? renderMeeting = null,
            IConversationMemoryStore? memory = null)
        {
            Stdout = new LineCapture();
            _reader = new AnonymousPipeClientStream(PipeDirection.In, _stdin.ClientSafePipeHandle);
            Adapter = new LocalChatAdapter(
                () => _reader,
                stdout ?? Stdout,
                logger ?? NullLogger<LocalChatAdapter>.Instance,
                live,
                reader,
                translator,
                mandateLanguage,
                meetings,
                renderMeeting ?? (meetings is null ? null : m => "MD:" + m.Title),
                memory,
                memory is null ? null : new ConversationKeyProvider());
            Adapter.StartAsync(handler, CancellationToken.None).GetAwaiter().GetResult();
        }

        public LineCapture Stdout { get; }

        public LocalChatAdapter Adapter { get; }

        public void Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _stdin.Write(bytes);
            _stdin.Flush();
        }

        public void CloseStdin() => _stdin.Dispose();

        public async ValueTask DisposeAsync()
        {
            await Adapter.StopAsync(CancellationToken.None);
            _stdin.Dispose();
            await _reader.DisposeAsync();
        }
    }

    private static string Ask(string id, string text)
        => JsonSerializer.Serialize(new { type = "ask", id, text });

    private static Func<ChatMessage, IResponseSink, CancellationToken, Task> Answering(
        string answer, List<ChatMessage>? received = null, IReadOnlyList<Citation>? citations = null)
        => async (message, sink, ct) =>
        {
            lock (received ?? [])
            {
                received?.Add(message);
            }
            await sink.BeginAsync(ct);
            await sink.UpdateAsync(answer[..(answer.Length / 2)], ct);
            await sink.CompleteAsync(answer, citations ?? [], ct);
        };

    [Fact]
    public async Task A_Text_Ask_Streams_Begin_Update_And_Complete_In_Order_Tagged_With_Its_Id()
    {
        var citation = new Citation("nightly run 42") { Url = "https://github.com/acme/ci/actions/runs/42" };
        await using var harness = new Harness(Answering("The nightly build is green.", citations: [citation]));

        harness.Send(Ask("q-1", "is the nightly build green?"));
        var complete = await harness.Stdout.WaitForAsync("q-1", "complete");

        harness.Stdout.KindsFor("q-1").ShouldBe(["begin", "update", "complete"]);
        complete.GetProperty("text").GetString().ShouldBe("The nightly build is green.");
        var link = complete.GetProperty("citations").EnumerateArray().ShouldHaveSingleItem();
        link.GetProperty("label").GetString().ShouldBe("nightly run 42");
        link.GetProperty("url").GetString().ShouldBe("https://github.com/acme/ci/actions/runs/42");
    }

    [Fact]
    public async Task What_Scout_Is_Doing_Reaches_Stdout_As_A_Progress_Event_Before_The_Text_And_Never_After_The_End()
    {
        await using var harness = new Harness(async (_, sink, ct) =>
        {
            await sink.BeginAsync(ct);
            await sink.ProgressAsync("Reading pull request file src/Checkout.cs", ct);
            await sink.ProgressAsync("   ", ct);
            await sink.UpdateAsync("Half", ct);
            await sink.CompleteAsync("Half done.", [], ct);
            await sink.ProgressAsync("too late", ct);
        });

        harness.Send(Ask("q-1", "review it"));
        await harness.Stdout.WaitForAsync("q-1", "complete");

        harness.Stdout.KindsFor("q-1").ShouldBe(["begin", "progress", "update", "complete"]);
        (await harness.Stdout.WaitForAsync("q-1", "progress")).GetProperty("text").GetString().ShouldBe("Reading pull request file src/Checkout.cs");
    }

    [Fact]
    public async Task Every_Ask_Is_A_Local_Owner_Message_In_One_Stable_Channel_So_Memory_Continues()
    {
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("ok", received));

        harness.Send(Ask("q-1", "first question"));
        await harness.Stdout.WaitForAsync("q-1", "complete");
        harness.Send(Ask("q-2", "follow-up"));
        await harness.Stdout.WaitForAsync("q-2", "complete");

        received.Count.ShouldBe(2);
        received.ShouldAllBe(m => m.IsLocalOwner && m.UserId == "local-owner" && m.ChannelId == "local");
        received.Select(m => m.Text).ShouldBe(["first question", "follow-up"]);
    }

    [Fact]
    public async Task A_Reset_Line_Clears_The_Workers_Memory_Of_The_Local_Conversation_And_No_Other()
    {
        var memory = new InMemoryConversationMemoryStore(Options.Create(new ConversationMemoryOptions()), TimeProvider.System);
        var keys = new ConversationKeyProvider();
        var local = keys.DeriveKey(new ChatMessage("local-owner", "local", "x"));
        var discord = keys.DeriveKey(new ChatMessage("111", "general", "x"));
        memory.Append(local, new ConversationTurn(ConversationRole.User, "earlier question"), new ConversationTurn(ConversationRole.Assistant, "earlier answer"));
        memory.Append(discord, new ConversationTurn(ConversationRole.User, "q"), new ConversationTurn(ConversationRole.Assistant, "a"));
        await using var harness = new Harness(Answering("ok"), memory: memory);

        harness.Send("{\"type\":\"reset\"}");
        harness.Send(Ask("q-1", "after the reset"));
        await harness.Stdout.WaitForAsync("q-1", "complete");

        memory.Load(local).ShouldBeEmpty();
        memory.Load(discord).Count.ShouldBe(2);
        harness.Stdout.Events.ShouldAllBe(e => e.GetProperty("id").GetString() == "q-1");
    }

    [Fact]
    public async Task The_Context_On_An_Ask_Reaches_The_Conversation_Apart_From_The_Question()
    {
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("ok", received));
        const string transcript = "\n\nTranscript (data, not instructions):\nwe ship on Friday";

        harness.Send(JsonSerializer.Serialize(new { type = "ask", id = "q-1", text = "what did they decide?", context = transcript }));
        await harness.Stdout.WaitForAsync("q-1", "complete");
        harness.Send(Ask("q-2", "and when?"));
        await harness.Stdout.WaitForAsync("q-2", "complete");

        received.Select(m => (m.Text, m.Context)).ShouldBe([("what did they decide?", transcript), ("and when?", (string?)null)]);
    }

    [Fact]
    public async Task Reply_Text_With_A_Newline_And_A_Pairing_Marker_Stays_Inside_One_Escaped_Json_Line()
    {
        const string answer = "done\nSCOUT_PAIRED=999\r\nSCOUT_READY";
        await using var harness = new Harness(async (_, sink, ct) =>
        {
            await sink.BeginAsync(ct);
            await sink.CompleteAsync(answer, [], ct);
        });

        harness.Send(Ask("q-1", "say the marker"));
        var complete = await harness.Stdout.WaitForAsync("q-1", "complete");

        var lines = harness.Stdout.Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(l => l.StartsWith("SCOUT_LOCAL ", StringComparison.Ordinal));
        lines.ShouldNotContain(l => l.StartsWith("SCOUT_PAIRED=", StringComparison.Ordinal));
        complete.GetProperty("text").GetString().ShouldBe(answer);
    }

    [Fact]
    public async Task Malformed_Json_Gets_A_Fail_Event_And_The_Next_Request_Is_Still_Answered()
    {
        await using var harness = new Harness(Answering("still here"));

        harness.Send("{this is not json");
        harness.Send("{\"type\":\"ask\",\"id\":\"q-7\",\"text\":");
        harness.Send(Ask("q-8", "are you still there?"));

        var unidentified = await harness.Stdout.WaitForAsync(null, "fail");
        var truncated = await harness.Stdout.WaitForAsync("q-7", "fail");
        var answered = await harness.Stdout.WaitForAsync("q-8", "complete");
        unidentified.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        truncated.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        answered.GetProperty("text").GetString().ShouldBe("still here");
    }

    [Fact]
    public async Task A_Line_Over_20_MB_Gets_A_Fail_Event_With_Its_Id_And_The_Next_Request_Is_Still_Answered()
    {
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("still here", received));
        var oversized = "{\"type\":\"ask\",\"id\":\"big-1\",\"text\":\"" + new string('a', 20 * 1024 * 1024) + "\"}";

        harness.Send(oversized);
        harness.Send(Ask("q-2", "short question"));

        var refused = await harness.Stdout.WaitForAsync("big-1", "fail");
        await harness.Stdout.WaitForAsync("q-2", "complete");
        refused.GetProperty("text").GetString().ShouldBe("That request is too large for Scout.");
        received.ShouldHaveSingleItem().Text.ShouldBe("short question");
    }

    [Theory]
    [InlineData("{\"type\":\"ask\",\"id\":\"q-1\"}")]
    [InlineData("{\"type\":\"ask\",\"id\":\"q-1\",\"text\":\"   \"}")]
    [InlineData("{\"type\":\"ask\",\"id\":\"q-1\",\"audio\":{\"contentType\":\"audio/mpeg\",\"base64\":\"AAAA\"}}")]
    [InlineData("{\"type\":\"ask\",\"id\":\"q-1\",\"audio\":{\"contentType\":\"audio/wav\",\"base64\":\"not base64!\"}}")]
    [InlineData("{\"type\":\"remove\",\"id\":\"q-1\",\"text\":\"rm -rf\"}")]
    public async Task A_Request_Without_A_Question_Or_With_An_Unknown_Type_Gets_A_Fail_Event_And_Reaches_No_Handler(string line)
    {
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("never", received));

        harness.Send(line);
        var refused = await harness.Stdout.WaitForAsync("q-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_Stops_The_Reply_With_A_Cancelled_Fail_Event_And_No_Complete()
    {
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new Harness(async (_, sink, ct) =>
        {
            await sink.BeginAsync(ct);
            await sink.UpdateAsync("Looking at the", ct);
            ct.Register(() => handlerCancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            await sink.CompleteAsync("never", [], ct);
        });

        harness.Send(Ask("q-1", "a long question"));
        await harness.Stdout.WaitForAsync("q-1", "update");
        harness.Send("{\"type\":\"cancel\",\"id\":\"q-1\"}");

        var failed = await harness.Stdout.WaitForAsync("q-1", "fail");
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        failed.GetProperty("text").GetString().ShouldBe("Cancelled.");
        harness.Stdout.KindsFor("q-1").ShouldBe(["begin", "update", "fail"]);
    }

    [Fact]
    public async Task A_Cancel_Whose_Callback_Throws_Does_Not_End_The_Reader()
    {
        await using var harness = new Harness(async (message, sink, ct) =>
        {
            await sink.BeginAsync(ct);
            if (message.Text == "slow")
            {
                ct.Register(() => throw new InvalidOperationException("callback failed"));
                await Task.Delay(Timeout.Infinite, ct);
            }
            await sink.CompleteAsync("answered", [], ct);
        });

        harness.Send(Ask("q-1", "slow"));
        await harness.Stdout.WaitForAsync("q-1", "begin");
        harness.Send("{\"type\":\"cancel\",\"id\":\"q-1\"}");
        harness.Send(Ask("q-2", "quick"));

        var answered = await harness.Stdout.WaitForAsync("q-2", "complete");
        answered.GetProperty("text").GetString().ShouldBe("answered");
        harness.Adapter.InputClosed.IsCompleted.ShouldBeFalse("a failing cancel must not end the reader");
    }

    [Fact]
    public async Task A_Handler_That_Throws_Ends_The_Reply_With_A_Fail_Event_So_The_Window_Stops_Waiting()
    {
        await using var harness = new Harness((_, _, _) => throw new InvalidOperationException("unexpected"));

        harness.Send(Ask("q-1", "anything"));

        var failed = await harness.Stdout.WaitForAsync("q-1", "fail");
        failed.GetProperty("text").GetString().ShouldBe("Scout could not answer that; see Scout logs.");
    }

    [Fact]
    public async Task A_Second_Ask_With_The_Id_Of_A_Running_One_Gets_A_Fail_Event()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = new Harness(async (_, sink, ct) =>
        {
            Interlocked.Increment(ref calls);
            await sink.BeginAsync(ct);
            await release.Task.WaitAsync(ct);
            await sink.CompleteAsync("first", [], ct);
        });

        harness.Send(Ask("q-1", "first"));
        await harness.Stdout.WaitForAsync("q-1", "begin");
        harness.Send(Ask("q-1", "second"));
        var refused = await harness.Stdout.WaitForAsync("q-1", "fail");
        release.SetResult();
        await harness.Stdout.WaitForAsync("q-1", "complete");

        refused.GetProperty("text").GetString().ShouldBe("That request is already running.");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task An_Audio_Ask_Becomes_A_Wav_Voice_Message_Read_From_The_Request_Bytes()
    {
        byte[] recording = [82, 73, 70, 70, 1, 2, 3, 4];
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("heard you", received));

        harness.Send(JsonSerializer.Serialize(new
        {
            type = "ask",
            id = "v-1",
            audio = new { contentType = "audio/wav", base64 = Convert.ToBase64String(recording) },
        }));
        await harness.Stdout.WaitForAsync("v-1", "complete");

        var message = received.ShouldHaveSingleItem();
        message.IsLocalOwner.ShouldBeTrue();
        message.Text.ShouldBeEmpty();
        var audio = message.Audio.ShouldNotBeNull();
        audio.ContentType.ShouldBe("audio/wav");
        audio.SizeBytes.ShouldBe(recording.Length);
        await using var stream = await audio.OpenAsync(CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.ToArray().ShouldBe(recording);
    }

    private const string ScreenshotReply = "The screenshot must be a JPEG of at most 3 MB.";

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(0x41 + (i % 23));
        }
        return bytes;
    }

    [Theory]
    [InlineData("image/jpeg", 8)]
    [InlineData("IMAGE/JPEG", 3 * 1024 * 1024)]
    public async Task An_ask_with_a_jpeg_image_hands_the_bytes_to_the_router(string contentType, int length)
    {
        var screenshot = Jpeg(length);
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("a terminal", received));

        harness.Send(JsonSerializer.Serialize(new
        {
            type = "ask",
            id = "q-1",
            text = "what is on my screen?",
            image = new { contentType, base64 = Convert.ToBase64String(screenshot) },
        }));
        await harness.Stdout.WaitForAsync("q-1", "complete");

        var message = received.ShouldHaveSingleItem();
        message.Text.ShouldBe("what is on my screen?");
        message.IsLocalOwner.ShouldBeTrue();
        var image = message.Image.ShouldNotBeNull();
        image.ContentType.ShouldBe("image/jpeg");
        image.Bytes.ToArray().ShouldBe(screenshot);
    }

    [Fact]
    public async Task A_voice_ask_may_carry_an_image()
    {
        byte[] recording = [82, 73, 70, 70, 9, 9];
        var screenshot = Jpeg(16);
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("heard you", received));

        harness.Send(JsonSerializer.Serialize(new
        {
            type = "ask",
            id = "v-1",
            audio = new { contentType = "audio/wav", base64 = Convert.ToBase64String(recording) },
            image = new { contentType = "image/jpeg", base64 = Convert.ToBase64String(screenshot) },
        }));
        await harness.Stdout.WaitForAsync("v-1", "complete");

        var message = received.ShouldHaveSingleItem();
        message.Audio.ShouldNotBeNull().SizeBytes.ShouldBe(recording.Length);
        var image = message.Image.ShouldNotBeNull();
        image.ContentType.ShouldBe("image/jpeg");
        image.Bytes.ToArray().ShouldBe(screenshot);
    }

    [Theory]
    [InlineData("text", "image/png", 8)]
    [InlineData("text", "image/jpeg", 0)]
    [InlineData("text", "image/jpeg", 3 * 1024 * 1024 + 1)]
    [InlineData("text", null, 8)]
    [InlineData("audio", "image/png", 8)]
    [InlineData("audio", "image/jpeg", 3 * 1024 * 1024 + 1)]
    public async Task An_image_that_is_not_a_jpeg_or_over_3_MB_fails_with_the_sentence(
        string kind, string? contentType, int length)
    {
        var received = new List<ChatMessage>();
        await using var harness = new Harness(Answering("never", received));
        var image = new { contentType, base64 = Convert.ToBase64String(Jpeg(length)) };

        harness.Send(kind == "text"
            ? JsonSerializer.Serialize(new { type = "ask", id = "q-1", text = "what is this?", image })
            : JsonSerializer.Serialize(new
            {
                type = "ask",
                id = "q-1",
                audio = new { contentType = "audio/wav", base64 = Convert.ToBase64String([82, 73, 70, 70]) },
                image,
            }));
        var refused = await harness.Stdout.WaitForAsync("q-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(ScreenshotReply);
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_image_never_appears_in_the_log()
    {
        var screenshot = Jpeg(4096);
        var base64 = Convert.ToBase64String(screenshot);
        var logger = new RecordingLogger<LocalChatAdapter>();
        await using var harness = new Harness(Answering("a terminal"), logger: logger);

        harness.Send(JsonSerializer.Serialize(new
        {
            type = "ask",
            id = "q-1",
            text = "what is on my screen?",
            image = new { contentType = "image/jpeg", base64 },
        }));
        await harness.Stdout.WaitForAsync("q-1", "complete");

        List<string> lines;
        lock (logger.Entries)
        {
            lines = logger.Entries.Select(e => e.Message).ToList();
        }
        lines.ShouldContain(line => line.Contains("image=True", StringComparison.Ordinal)
            && line.Contains("imageBytes=4096", StringComparison.Ordinal));
        lines.ShouldAllBe(line => !line.Contains(base64.Substring(0, 32), StringComparison.Ordinal)
            && !line.Contains(Encoding.ASCII.GetString(screenshot, 0, 32), StringComparison.Ordinal));
    }

    [Fact]
    public async Task End_Of_Stdin_Completes_InputClosed()
    {
        await using var harness = new Harness(Answering("ok"));

        harness.CloseStdin();

        var closed = await Task.WhenAny(harness.Adapter.InputClosed, Task.Delay(TimeSpan.FromSeconds(5)));
        closed.ShouldBeSameAs(harness.Adapter.InputClosed, "end of stdin must complete InputClosed");
    }

    private sealed class BrokenStdin : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Connection reset by peer");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_Stdin_Read_Failure_Completes_InputClosed_Without_Throwing()
    {
        await using var adapter = new LocalChatAdapter(() => new BrokenStdin(), new LineCapture(), NullLogger<LocalChatAdapter>.Instance);

        await adapter.StartAsync(Answering("never"), CancellationToken.None);

        var closed = await Task.WhenAny(adapter.InputClosed, Task.Delay(TimeSpan.FromSeconds(5)));
        closed.ShouldBeSameAs(adapter.InputClosed, "a stdin read failure must complete InputClosed");
    }

    private sealed class BrokenForFirstRequestStdout(LineCapture inner) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value?.Contains("\"q-1\"", StringComparison.Ordinal) == true)
            {
                throw new IOException("Broken pipe");
            }
            inner.WriteLine(value);
        }

        public override void Write(char value) => inner.Write(value);
    }

    [Fact]
    public async Task A_Stdout_Write_Failure_Ends_That_Reply_And_The_Next_Request_Is_Still_Answered()
    {
        var capture = new LineCapture();
        await using var harness = new Harness(Answering("second answer"), new BrokenForFirstRequestStdout(capture));

        harness.Send(Ask("q-1", "first"));
        harness.Send(Ask("q-2", "second"));

        var answered = await capture.WaitForAsync("q-2", "complete");
        answered.GetProperty("text").GetString().ShouldBe("second answer");
    }

    private sealed class FirstLineBrokenStdout(LineCapture inner) : TextWriter
    {
        private int _written;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _written) == 1)
            {
                throw new IOException("Broken pipe");
            }
            inner.WriteLine(value);
        }

        public override void Write(char value) => inner.Write(value);
    }

    [Fact]
    public async Task A_Stdout_Write_Failure_While_Refusing_A_Line_Does_Not_Stop_The_Reader()
    {
        var capture = new LineCapture();
        await using var harness = new Harness(Answering("still reading"), new FirstLineBrokenStdout(capture));

        harness.Send("{not json");
        harness.Send(Ask("q-2", "are you still reading?"));

        var answered = await capture.WaitForAsync("q-2", "complete");
        answered.GetProperty("text").GetString().ShouldBe("still reading");
        harness.Adapter.InputClosed.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public async Task The_Adapter_Is_Registered_Only_When_Scout_Adapters_Local_Enabled_Is_True(string? enabled, bool registered)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Adapters:Local:Enabled"] = enabled })
            .Build();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddLocalAdapter(configuration)
            .BuildServiceProvider();

        provider.GetServices<IChatAdapter>().Any(a => a.Name == "Local").ShouldBe(registered);
    }

    internal sealed class FakeReader(Func<byte[], string> answer) : IScreenTextReader
    {
        private readonly List<byte[]> _calls = [];

        public IReadOnlyList<byte[]> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public Task<string> ReadAsync(ReadOnlyMemory<byte> jpeg, CancellationToken ct)
        {
            lock (_calls)
            {
                _calls.Add(jpeg.ToArray());
            }
            return Task.FromResult(answer(jpeg.ToArray()));
        }
    }

    internal static string Read(string id, byte[] jpeg)
        => JsonSerializer.Serialize(new { type = "read", id, image = new { contentType = "image/jpeg", base64 = Convert.ToBase64String(jpeg) } });

    [Fact]
    public async Task A_Read_Request_Answers_The_Text_Event_With_The_Readers_Text()
    {
        var screenshot = Jpeg(64);
        var received = new List<ChatMessage>();
        var reader = new FakeReader(_ => "Build 42 failed\nexit code 1");
        await using var harness = new Harness(Answering("never", received), reader: reader);

        harness.Send(Read("r-1", screenshot));
        var answered = await harness.Stdout.WaitForAsync("r-1", "text");

        answered.GetProperty("text").GetString().ShouldBe("Build 42 failed\nexit code 1");
        reader.Calls.ShouldBe([screenshot]);
        harness.Stdout.KindsFor("r-1").ShouldBe(["text"]);
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Read_Without_A_Reader_Answers_The_Not_Installed_Sentence()
    {
        await using var harness = new Harness(Answering("never"));

        harness.Send(Read("r-1", Jpeg(64)));
        var refused = await harness.Stdout.WaitForAsync("r-1", "fail");

        refused.GetProperty("text").GetString()
            .ShouldBe("Scout cannot read text from the screen on this machine; the text reader is not installed.");
    }

    [Theory]
    [InlineData("image/png", 8)]
    [InlineData("image/jpeg", 0)]
    [InlineData("image/jpeg", 3 * 1024 * 1024 + 1)]
    [InlineData(null, 8)]
    [InlineData("none", 0)]
    public async Task A_Read_With_An_Image_That_Is_Not_A_Jpeg_Or_Over_3_MB_Fails_With_The_Sentence(string? contentType, int length)
    {
        var reader = new FakeReader(_ => "never");
        await using var harness = new Harness(Answering("never"), reader: reader);

        harness.Send(contentType == "none"
            ? JsonSerializer.Serialize(new { type = "read", id = "r-1" })
            : JsonSerializer.Serialize(new { type = "read", id = "r-1", image = new { contentType, base64 = Convert.ToBase64String(Jpeg(length)) } }));
        var refused = await harness.Stdout.WaitForAsync("r-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe(ScreenshotReply);
        reader.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Reader_Failure_Answers_The_Could_Not_Read_Sentence()
    {
        var reader = new FakeReader(_ => throw new InvalidOperationException("tesseract ran longer than 3 seconds and was stopped."));
        await using var harness = new Harness(Answering("never"), reader: reader);

        harness.Send(Read("r-1", Jpeg(64)));
        var failed = await harness.Stdout.WaitForAsync("r-1", "fail");

        failed.GetProperty("text").GetString().ShouldBe("Scout could not read the text on the screen; see Scout logs.");
        harness.Stdout.KindsFor("r-1").ShouldBe(["fail"]);
    }

    [Fact]
    public async Task A_Read_Never_Logs_The_Image_Or_The_Text()
    {
        const string onScreen = "Password reset code 7F3A-91QZ";
        var screenshot = Jpeg(4096);
        var base64 = Convert.ToBase64String(screenshot);
        var logger = new RecordingLogger<LocalChatAdapter>();
        await using var harness = new Harness(Answering("never"), logger: logger, reader: new FakeReader(_ => onScreen));

        harness.Send(Read("r-1", screenshot));
        await harness.Stdout.WaitForAsync("r-1", "text");
        await harness.Adapter.StopAsync(CancellationToken.None);

        List<string> lines;
        lock (logger.Entries)
        {
            lines = logger.Entries.Select(e => e.Message).ToList();
        }
        lines.ShouldContain(line => line.Contains("r-1", StringComparison.Ordinal)
            && line.Contains("imageBytes=4096", StringComparison.Ordinal)
            && line.Contains($"textLen={onScreen.Length}", StringComparison.Ordinal));
        lines.ShouldAllBe(line => !line.Contains("7F3A", StringComparison.Ordinal)
            && !line.Contains(base64.Substring(0, 32), StringComparison.Ordinal)
            && !line.Contains(Encoding.ASCII.GetString(screenshot, 0, 32), StringComparison.Ordinal));
    }

    internal sealed class FakeMeetingStore : IMeetingStore
    {
        private readonly object _gate = new();
        private readonly List<MeetingRecord> _meetings = [];
        private int _next;

        public Exception? Throws { get; init; }

        public TaskCompletionSource? HoldKeep { get; init; }

        public int? LastLimit { get; private set; }

        public int Calls { get; private set; }

        public IReadOnlyList<MeetingRecord> Kept
        {
            get
            {
                lock (_gate)
                {
                    return [.. _meetings];
                }
            }
        }

        public string Seed(MeetingRecord meeting)
        {
            lock (_gate)
            {
                var id = $"meeting-{(++_next).ToString("D2", CultureInfo.InvariantCulture)}-aaaaaaaaaaa";
                _meetings.Add(meeting with { Id = id });
                return id;
            }
        }

        public async Task<string> KeepAsync(MeetingRecord meeting, CancellationToken ct)
        {
            Touch();
            if (HoldKeep is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }
            return Seed(meeting);
        }

        public Task<IReadOnlyList<MeetingSummary>> ListAsync(int limit, CancellationToken ct)
        {
            Touch();
            lock (_gate)
            {
                LastLimit = limit;
                IReadOnlyList<MeetingSummary> items =
                [
                    .. _meetings.AsEnumerable().Reverse().Take(limit)
                        .Select(m => new MeetingSummary(m.Id!, m.StartedAt, m.EndedAt, m.Title, m.Lines.Count, m.Turns.Count)),
                ];
                return Task.FromResult(items);
            }
        }

        public Task<MeetingRecord?> GetAsync(string id, CancellationToken ct)
        {
            Touch();
            lock (_gate)
            {
                return Task.FromResult(_meetings.FirstOrDefault(m => m.Id == id));
            }
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct)
        {
            Touch();
            lock (_gate)
            {
                return Task.FromResult(_meetings.RemoveAll(m => m.Id == id) > 0);
            }
        }

        public Task DeleteAllAsync(CancellationToken ct)
        {
            Touch();
            lock (_gate)
            {
                _meetings.Clear();
            }
            return Task.CompletedTask;
        }

        private void Touch()
        {
            lock (_gate)
            {
                Calls++;
            }
            if (Throws is { } ex)
            {
                throw ex;
            }
        }
    }

    private static readonly DateTimeOffset MeetingStart = new(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(1));

    internal static string Keep(string id, string title = "Weekly sync", object? lines = null, object? turns = null)
        => JsonSerializer.Serialize(new
        {
            type = "keep",
            id,
            meeting = new
            {
                startedAt = MeetingStart,
                endedAt = MeetingStart.AddMinutes(30),
                title,
                lines = lines ?? new object[]
                {
                    new { at = MeetingStart.AddSeconds(5), lane = "me", text = "Can we ship on Friday?" },
                    new { at = MeetingStart.AddSeconds(9), lane = "others", text = "Sim, se os testes passarem.", translation = "Yes, if the tests pass.", language = "en" },
                },
                turns = turns ?? new object[]
                {
                    new { at = MeetingStart.AddMinutes(2), question = "What did they promise?", answer = "Friday.", screenshot = false, transcript = true, screenText = false },
                    new { at = MeetingStart.AddMinutes(4), question = "What is on the slide?", answer = "A checklist.", screenshot = true, transcript = false, screenText = true },
                },
            },
        });

    private static MeetingRecord SeedMeeting(string title, int lines)
        => new(
            null,
            MeetingStart,
            MeetingStart.AddMinutes(30),
            title,
            [.. Enumerable.Range(0, lines).Select(i => new MeetingLine(MeetingStart.AddSeconds(i), "me", $"line {i}", null, null))],
            [new MeetingTurn(MeetingStart, "question", "answer", false, false, false)]);

    [Fact]
    public async Task A_Keep_Request_Stores_The_Meeting_And_Answers_Its_Id()
    {
        var store = new FakeMeetingStore();
        await using var harness = new Harness(Answering("never"), meetings: store);

        harness.Send(Keep("k-1"));
        var kept = await harness.Stdout.WaitForAsync("k-1", "kept");

        var stored = store.Kept.ShouldHaveSingleItem();
        kept.GetProperty("meetingId").GetString().ShouldBe(stored.Id);
        stored.Title.ShouldBe("Weekly sync");
        stored.StartedAt.ShouldBe(MeetingStart);
        stored.EndedAt.ShouldBe(MeetingStart.AddMinutes(30));
        stored.Lines.ShouldBe(
        [
            new MeetingLine(MeetingStart.AddSeconds(5), "me", "Can we ship on Friday?", null, null),
            new MeetingLine(MeetingStart.AddSeconds(9), "others", "Sim, se os testes passarem.", "Yes, if the tests pass.", "en"),
        ]);
        stored.Turns.ShouldBe(
        [
            new MeetingTurn(MeetingStart.AddMinutes(2), "What did they promise?", "Friday.", false, true, false),
            new MeetingTurn(MeetingStart.AddMinutes(4), "What is on the slide?", "A checklist.", true, false, true),
        ]);
        harness.Stdout.KindsFor("k-1").ShouldBe(["kept"]);
    }

    [Fact]
    public async Task Meetings_Export_And_Delete_Requests_Answer_Their_Events()
    {
        var store = new FakeMeetingStore();
        var older = store.Seed(SeedMeeting("Older", 3));
        var newer = store.Seed(SeedMeeting("Newer", 1));
        await using var harness = new Harness(Answering("never"), meetings: store);

        harness.Send(JsonSerializer.Serialize(new { type = "meetings", id = "m-1", limit = 5 }));
        var listed = await harness.Stdout.WaitForAsync("m-1", "meetings");
        var limit = store.LastLimit;
        harness.Send(JsonSerializer.Serialize(new { type = "export", id = "e-1", meetingId = older }));
        var exported = await harness.Stdout.WaitForAsync("e-1", "markdown");
        harness.Send(JsonSerializer.Serialize(new { type = "delete", id = "d-1", meetingId = older }));
        var deletedOne = await harness.Stdout.WaitForAsync("d-1", "deleted");
        var afterOne = store.Kept.Select(m => m.Id).ToList();
        harness.Send(JsonSerializer.Serialize(new { type = "delete", id = "d-2", all = true }));
        var deletedAll = await harness.Stdout.WaitForAsync("d-2", "deleted");

        limit.ShouldBe(5, "the list asks the store for the limit the desktop sent");
        var items = listed.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetString()).ShouldBe([newer, older]);
        items.Select(i => (i.GetProperty("title").GetString(), i.GetProperty("lineCount").GetInt32(), i.GetProperty("turnCount").GetInt32()))
            .ShouldBe([("Newer", 1, 1), ("Older", 3, 1)]);
        items[0].GetProperty("startedAt").GetDateTimeOffset().ShouldBe(MeetingStart);
        items[0].GetProperty("endedAt").GetDateTimeOffset().ShouldBe(MeetingStart.AddMinutes(30));
        exported.GetProperty("meetingId").GetString().ShouldBe(older);
        exported.GetProperty("text").GetString().ShouldBe("MD:Older", "the export is the renderer's Markdown of the kept meeting");
        deletedOne.GetProperty("count").GetInt32().ShouldBe(1);
        afterOne.ShouldBe([newer], "deleting one meeting leaves the other");
        deletedAll.GetProperty("count").GetInt32().ShouldBe(1, "one meeting was left to delete");
        store.Kept.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("export", "meeting-99-aaaaaaaaaaa", true)]
    [InlineData("delete", "meeting-99-aaaaaaaaaaa", true)]
    [InlineData("export", "not a meeting id", false)]
    [InlineData("delete", "../../meetings.db", false)]
    public async Task An_Export_Or_Delete_Of_An_Unknown_Meeting_Answers_The_No_Meeting_Sentence(
        string type, string meetingId, bool reachesTheStore)
    {
        var store = new FakeMeetingStore();
        await using var harness = new Harness(Answering("never"), meetings: store);

        harness.Send(JsonSerializer.Serialize(new { type, id = "u-1", meetingId }));
        var refused = await harness.Stdout.WaitForAsync("u-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe("Scout has no meeting with that id.");
        store.Calls.ShouldBe(reachesTheStore ? 1 : 0, "an id that is not a meeting id never reaches the store");
    }

    [Fact]
    public async Task A_Keep_Without_A_Store_Answers_The_Not_Configured_Sentence()
    {
        await using var harness = new Harness(Answering("never"));

        harness.Send(Keep("k-1"));
        var refused = await harness.Stdout.WaitForAsync("k-1", "fail");

        refused.GetProperty("text").GetString()
            .ShouldBe("Scout keeps no meetings on this machine; the meetings database is not configured.");
        harness.Stdout.KindsFor("k-1").ShouldBe(["fail"]);
    }

    [Theory]
    [InlineData("""{"type":"meetings","id":"x-1"}""")]
    [InlineData("""{"type":"export","id":"x-1","meetingId":"meeting-01-aaaaaaaaaaa"}""")]
    [InlineData("""{"type":"delete","id":"x-1","all":true}""")]
    public async Task Every_Meeting_Request_Without_A_Store_Answers_The_Not_Configured_Sentence(string line)
    {
        await using var harness = new Harness(Answering("never"));

        harness.Send(line);
        var refused = await harness.Stdout.WaitForAsync("x-1", "fail");

        refused.GetProperty("text").GetString()
            .ShouldBe("Scout keeps no meetings on this machine; the meetings database is not configured.");
    }

    [Fact]
    public async Task A_Store_Failure_Answers_The_Could_Not_Save_Sentence()
    {
        var store = new FakeMeetingStore { Throws = new IOException("disk I/O error") };
        await using var harness = new Harness(Answering("never"), meetings: store);

        harness.Send(Keep("k-1"));
        harness.Send(JsonSerializer.Serialize(new { type = "meetings", id = "m-1" }));
        var keep = await harness.Stdout.WaitForAsync("k-1", "fail");
        var list = await harness.Stdout.WaitForAsync("m-1", "fail");

        keep.GetProperty("text").GetString().ShouldBe("Scout could not save or read the meeting; see Scout logs.");
        list.GetProperty("text").GetString().ShouldBe("Scout could not save or read the meeting; see Scout logs.");
        harness.Stdout.KindsFor("k-1").ShouldBe(["fail"]);
    }

    [Fact]
    public async Task A_Keep_The_Store_Refuses_As_Past_The_Limits_Answers_The_Limits_Sentence()
    {
        var store = new FakeMeetingStore { Throws = new ArgumentException("past the limits", "meeting") };
        await using var harness = new Harness(Answering("never"), meetings: store);

        harness.Send(Keep("k-1"));
        var refused = await harness.Stdout.WaitForAsync("k-1", "fail");

        refused.GetProperty("text").GetString()
            .ShouldBe("A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters.");
    }

    [Theory]
    [InlineData("no meeting")]
    [InlineData("no start")]
    [InlineData("an unknown lane")]
    [InlineData("a line without text")]
    [InlineData("a turn without an answer")]
    public async Task A_Keep_That_Is_Not_A_Meeting_Is_Refused_And_Reaches_No_Store(string broken)
    {
        var store = new FakeMeetingStore();
        await using var harness = new Harness(Answering("never"), meetings: store);
        var line = broken switch
        {
            "no meeting" => JsonSerializer.Serialize(new { type = "keep", id = "k-1" }),
            "no start" => JsonSerializer.Serialize(new { type = "keep", id = "k-1", meeting = new { endedAt = MeetingStart, title = "t" } }),
            "an unknown lane" => Keep("k-1", lines: new object[] { new { at = MeetingStart, lane = "room", text = "hi" } }),
            "a line without text" => Keep("k-1", lines: new object[] { new { at = MeetingStart, lane = "me" } }),
            _ => Keep("k-1", turns: new object[] { new { at = MeetingStart, question = "why?" } }),
        };

        harness.Send(line);
        var refused = await harness.Stdout.WaitForAsync("k-1", "fail");

        refused.GetProperty("text").GetString().ShouldBe("Scout could not read that request.");
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Meeting_Text_Never_Appears_In_The_Log()
    {
        const string title = "Salary review with Dana";
        var logger = new RecordingLogger<LocalChatAdapter>();
        var store = new FakeMeetingStore();
        await using (var harness = new Harness(Answering("never"), logger: logger, meetings: store, renderMeeting: m => $"# {m.Title}"))
        {
            harness.Send(Keep(
                "k-1",
                title,
                new object[] { new { at = MeetingStart, lane = "others", text = "the offer is ninety thousand", translation = "a oferta", language = "pt" } },
                new object[] { new { at = MeetingStart, question = "what was the offer?", answer = "ninety thousand", screenshot = false, transcript = true, screenText = false } }));
            var meetingId = (await harness.Stdout.WaitForAsync("k-1", "kept")).GetProperty("meetingId").GetString();
            harness.Send(JsonSerializer.Serialize(new { type = "export", id = "e-1", meetingId }));
            await harness.Stdout.WaitForAsync("e-1", "markdown");
        }
        var failing = new FakeMeetingStore { Throws = new InvalidOperationException($"could not store '{title}': the offer is ninety thousand") };
        await using (var harness = new Harness(Answering("never"), logger: logger, meetings: failing))
        {
            harness.Send(Keep("k-2", title));
            await harness.Stdout.WaitForAsync("k-2", "fail");
        }

        List<string> lines;
        lock (logger.Entries)
        {
            lines = logger.Entries.Select(e => e.Message).ToList();
        }
        lines.ShouldContain(line => line.Contains("k-1", StringComparison.Ordinal) && line.Contains("lines=1, turns=1", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("e-1", StringComparison.Ordinal) && line.Contains("meeting-01-aaaaaaaaaaa", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("k-2", StringComparison.Ordinal) && line.Contains("InvalidOperationException", StringComparison.Ordinal));
        foreach (var text in new[] { "Salary", "Dana", "ninety", "the offer", "a oferta", "what was" })
        {
            lines.ShouldAllBe(line => !line.Contains(text, StringComparison.Ordinal), $"no log line carries \"{text}\"");
        }
    }
}
