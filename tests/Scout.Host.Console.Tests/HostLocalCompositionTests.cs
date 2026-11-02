using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Adapter.Local;
using Scout.Conversation.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostLocalCompositionTests
{
    private static IHost Build(string? localEnabled)
        => new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = localEnabled,
                    })
                    .Build());
            })
            .Build();

    private static ServiceProvider Watchdog(string? localEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Adapters:Local:Enabled"] = localEnabled })
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddLocalAdapter(configuration)
            .AddParentWatchdog("1")
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void The_Host_Registers_The_Local_Adapter_Only_When_It_Is_Enabled(string? enabled, bool registered)
    {
        using var host = Build(enabled);

        host.Services.GetServices<IChatAdapter>().Any(a => a.Name == "Local").ShouldBe(registered);
    }

    [Fact]
    public void With_Only_The_Local_Adapter_Enabled_The_Host_Builds_And_Local_Is_The_Only_Adapter()
    {
        using var host = Build("true");

        host.Services.GetServices<IChatAdapter>().Select(a => a.Name).ShouldBe(["Local"]);
        host.Services.GetServices<IHostedService>().ShouldContain(s => s is ConversationWorker);
    }

    [Fact]
    public void The_Local_Adapter_Starts_Before_The_Network_Adapters_So_The_Watchdog_Reads_Stdin_From_The_Start()
    {
        using var host = new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = "true",
                        ["Scout:Adapters:Discord:BotToken"] = "discord-dummy-not-real",
                    })
                    .Build());
            })
            .Build();

        host.Services.GetServices<IChatAdapter>().Select(a => a.Name).ShouldBe(["Local", "Discord"]);
    }

    [Fact]
    public async Task With_The_Watchdog_On_And_The_Local_Adapter_Enabled_The_Watchdog_Waits_On_The_Local_Adapters_Stdin()
    {
        await using var provider = Watchdog("true");

        provider.GetRequiredService<IParentLivenessProbe>().ShouldBeOfType<InputClosedParentLivenessProbe>();
    }

    [Fact]
    public async Task With_The_Watchdog_On_And_No_Local_Adapter_The_Watchdog_Reads_Stdin_Itself()
    {
        await using var provider = Watchdog(null);

        provider.GetRequiredService<IParentLivenessProbe>().ShouldBeOfType<StdinEofParentLivenessProbe>();
    }

    [Fact]
    public async Task The_Input_Closed_Probe_Reports_The_Parent_Gone_When_The_Local_Adapters_Input_Closes()
    {
        var closed = new TaskCompletionSource();
        var probe = new InputClosedParentLivenessProbe(() => closed.Task);

        var waiting = probe.WaitForParentExitAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.ShouldBeFalse("the parent is alive while stdin is open");

        closed.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        waiting.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task The_Composed_Host_Answers_A_Local_Owner_Even_When_No_Chat_User_Is_Allowed()
    {
        using var host = new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Access:RequireAllowList"] = "true",
                    })
                    .Build());
            })
            .Build();
        var conductor = host.Services.GetRequiredService<Scout.Conversation.ConversationConductor>();
        var sink = new FinalTextSink();
        var audio = new AudioAttachment(
            "audio/wav", 16, null, _ => Task.FromResult<Stream>(new MemoryStream(new byte[16])));

        await conductor.HandleAsync(
            new ChatMessage(LocalChatAdapter.OwnerUserId, LocalChatAdapter.ChannelId, string.Empty, null, audio) { IsLocalOwner = true },
            sink,
            CancellationToken.None);

        sink.FinalText.ShouldBe(
            "Voice messages need a Whisper model. Download one in the Scout app under AI Sources, Local Models.");
    }

    private sealed class FinalTextSink : IResponseSink
    {
        public string? FinalText { get; private set; }

        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
        {
            FinalText = finalText;
            return Task.CompletedTask;
        }

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
