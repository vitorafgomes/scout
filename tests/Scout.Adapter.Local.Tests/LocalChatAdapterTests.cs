using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local.Tests;

public sealed class LocalChatAdapterTests
{
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _reader;

        public Harness(Func<ChatMessage, IResponseSink, CancellationToken, Task> handler, TextWriter? stdout = null)
        {
            Stdout = new LineCapture();
            _reader = new AnonymousPipeClientStream(PipeDirection.In, _stdin.ClientSafePipeHandle);
            Adapter = new LocalChatAdapter(() => _reader, stdout ?? Stdout, NullLogger<LocalChatAdapter>.Instance);
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
    [InlineData("{\"type\":\"delete\",\"id\":\"q-1\",\"text\":\"rm -rf\"}")]
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
}
