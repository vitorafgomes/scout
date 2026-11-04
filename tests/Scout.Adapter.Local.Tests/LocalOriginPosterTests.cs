using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local.Tests;

public sealed class LocalOriginPosterTests
{
    private sealed class BrokenStdout : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw new IOException("Broken pipe");
    }

    private static Task Ignore(ChatMessage message, IResponseSink sink, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task Every_Ask_Carries_The_Local_Session_As_Its_Origin()
    {
        var received = new List<ChatMessage>();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new LocalChatAdapterTests.Harness((message, _, _) =>
        {
            lock (received)
            {
                received.Add(message);
                if (received.Count == 2)
                {
                    answered.TrySetResult();
                }
            }
            return Task.CompletedTask;
        });

        harness.Send(JsonSerializer.Serialize(new { type = "ask", id = "q-1", text = "start the weekly video" }));
        harness.Send(JsonSerializer.Serialize(new
        {
            type = "ask",
            id = "q-2",
            audio = new { contentType = "audio/wav", base64 = Convert.ToBase64String([1, 2, 3]) },
        }));
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        received.Select(m => m.OriginReference).ShouldBe(["local:session", "local:session"]);
    }

    [Fact]
    public async Task A_Session_Reference_Writes_One_Notice_Line_With_A_Null_Id()
    {
        await using var harness = new LocalChatAdapterTests.Harness(Ignore);

        var posted = await new LocalOriginPoster(harness.Adapter, NullLogger<LocalOriginPoster>.Instance)
            .PostAsync("session", "Script ready for review.", CancellationToken.None);

        posted.ShouldBeTrue();
        harness.Stdout.Lines.ShouldBe(["SCOUT_LOCAL {\"id\":null,\"event\":\"notice\",\"text\":\"Script ready for review.\"}"]);
    }

    [Fact]
    public async Task A_Text_Over_32000_Characters_Is_Capped_To_32000()
    {
        await using var harness = new LocalChatAdapterTests.Harness(Ignore);

        await new LocalOriginPoster(harness.Adapter, NullLogger<LocalOriginPoster>.Instance)
            .PostAsync("session", new string('a', 40_000), CancellationToken.None);

        var text = harness.Stdout.Events.ShouldHaveSingleItem().GetProperty("text").GetString()!;
        text.Length.ShouldBe(32_000);
        text.ShouldEndWith("...");
    }

    [Fact]
    public async Task When_Stdout_Cannot_Be_Written_It_Returns_False_With_A_Warning()
    {
        await using var harness = new LocalChatAdapterTests.Harness(Ignore, new BrokenStdout());
        var logger = new RecordingLogger<LocalOriginPoster>();

        var posted = await new LocalOriginPoster(harness.Adapter, logger)
            .PostAsync("session", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task An_Adapter_That_Was_Stopped_Posts_Nothing()
    {
        var harness = new LocalChatAdapterTests.Harness(Ignore);
        await harness.DisposeAsync();

        var posted = await new LocalOriginPoster(harness.Adapter, NullLogger<LocalOriginPoster>.Instance)
            .PostAsync("session", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse();
        harness.Stdout.Lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("q-1")]
    [InlineData("Session")]
    public async Task A_Payload_Other_Than_Session_Posts_Nothing(string payload)
    {
        await using var harness = new LocalChatAdapterTests.Harness(Ignore);

        (await new LocalOriginPoster(harness.Adapter, NullLogger<LocalOriginPoster>.Instance)
            .PostAsync(payload, "Upload finished.", CancellationToken.None)).ShouldBeFalse();
        harness.Stdout.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_The_Local_Adapter_Enabled_A_Local_Reference_Reaches_The_Local_Adapter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Adapters:Local:Enabled"] = "true" })
            .Build();
        var logger = new RecordingLogger<LocalOriginPoster>();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILogger<LocalOriginPoster>>(logger)
            .AddLocalAdapter(configuration)
            .AddSingleton<IOriginNotifier, OriginNotifier>()
            .BuildServiceProvider();

        var posted = await provider.GetRequiredService<IOriginNotifier>()
            .PostAsync("local:session", "Upload finished.", CancellationToken.None);

        posted.ShouldBeFalse("the adapter was never started");
        logger.Entries.ShouldHaveSingleItem().Message
            .ShouldBe("The local adapter is not running; nothing was posted to the desktop.");
    }
}
