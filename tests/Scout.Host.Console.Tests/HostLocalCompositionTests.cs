using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Scout.Adapter.Local;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;
using Scout.Meetings;
using Scout.Operator;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostLocalCompositionTests
{
    private static IHost Build(string? localEnabled, string? whisperModelPath = null)
        => new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = localEnabled,
                        ["Scout:Transcription:ModelPath"] = whisperModelPath,
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
    public void With_A_Whisper_Model_File_The_Local_Adapter_Receives_The_Live_Transcriber()
    {
        var model = Path.Combine(Path.GetTempPath(), $"scout-fake-ggml-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(model, new byte[1024]);
        try
        {
            using var host = Build("true", model);

            var adapter = host.Services.GetRequiredService<LocalChatAdapter>();
            adapter.LiveTranscriber.ShouldNotBeNull().ShouldBeSameAs(host.Services.GetRequiredService<ILiveTranscriber>());
        }
        finally
        {
            File.Delete(model);
        }
    }

    [Fact]
    public void The_Local_Adapter_Receives_The_Conversation_Memory_So_A_Reset_Clears_What_The_Router_Replays()
    {
        using var host = Build("true", "/nonexistent/scout-test/ggml-base.bin");

        var adapter = host.Services.GetRequiredService<LocalChatAdapter>();
        adapter.MemoryStore.ShouldNotBeNull().ShouldBeSameAs(host.Services.GetRequiredService<IConversationMemoryStore>());
        adapter.KeyProvider.ShouldNotBeNull().ShouldBeSameAs(host.Services.GetRequiredService<IConversationKeyProvider>());
        adapter.CarriedSkills.ShouldNotBeNull().ShouldBeSameAs(host.Services.GetRequiredService<Scout.Conversation.Abstractions.IConversationRouter>());
    }

    [Fact]
    public void Without_A_Whisper_Model_File_The_Local_Adapter_Gets_No_Live_Transcriber()
    {
        using var host = Build("true", "/nonexistent/scout-test/ggml-base.bin");

        host.Services.GetService<ILiveTranscriber>().ShouldBeNull();
        host.Services.GetRequiredService<LocalChatAdapter>().LiveTranscriber.ShouldBeNull();
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
            "Voice messages need a Whisper model. Download one in the Scout app under Models, Transcription.");
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_Local_Adapter_Receives_The_Screen_Text_Reader_Only_When_Tesseract_And_Its_Data_Exist(bool program, bool data)
    {
        var root = Directory.CreateTempSubdirectory("scout-host-tesseract-").FullName;
        try
        {
            var tesseract = Path.Combine(root, "tesseract");
            var tessdata = Path.Combine(root, "tessdata");
            if (program)
            {
                File.WriteAllBytes(tesseract, [1]);
            }
            if (data)
            {
                Directory.CreateDirectory(tessdata);
            }
            using var host = new HostBuilder()
                .ConfigureServices((_, services) => services.AddLogging().AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = "true",
                        ["Scout:Tools:TesseractPath"] = tesseract,
                        ["Scout:Tools:TessdataPath"] = tessdata,
                    })
                    .Build()))
                .Build();

            var reader = host.Services.GetRequiredService<LocalChatAdapter>().ScreenTextReader;

            (reader is not null).ShouldBe(program && data);
            reader.ShouldBeSameAs(host.Services.GetService<IScreenTextReader>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Ollama", true)]
    [InlineData("None", false)]
    public void The_Local_Adapter_Receives_The_Live_Translator_Only_With_An_AI_Source(string provider, bool translates)
    {
        using var host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddLogging().AddScoutHost(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Llm:Provider"] = provider,
                    ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                    ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                    ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                    ["Scout:Adapters:Local:Enabled"] = "true",
                    ["Scout:Plugins:YouTube:ApiKey"] = "youtube-dummy-not-real",
                })
                .Build()))
            .Build();

        var translator = host.Services.GetRequiredService<LocalChatAdapter>().Translator;

        (translator is not null).ShouldBe(translates);
        translator.ShouldBeSameAs(host.Services.GetService<ILiveTranslator>());
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("", null)]
    public async Task With_The_Operator_Wired_The_Local_Adapter_Reads_The_Mandate_Language_Through_Its_Control_Plane(
        string mandateLanguage, string? expected)
    {
        var controlPlane = Substitute.For<IOperatorControlPlane>();
        controlPlane.GetMandateAsync(Arg.Any<CancellationToken>())
            .Returns(new OperatorMandate("", "", [], "", mandateLanguage, "", [], "", ""));
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddScoutHost(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Llm:Provider"] = "None",
                    ["Scout:Adapters:Local:Enabled"] = "true",
                    ["Scout:Plugins:YouTube:ApiKey"] = "youtube-dummy-not-real",
                })
                .Build())
            .AddSingleton(controlPlane)
            .BuildServiceProvider();

        var mandate = provider.GetRequiredService<LocalChatAdapter>().MandateLanguage.ShouldNotBeNull();

        (await mandate(CancellationToken.None)).ShouldBe(expected);
    }

    [Fact]
    public void Without_The_Operator_The_Local_Adapter_Has_No_Mandate_Language()
    {
        using var host = Build("true");

        host.Services.GetRequiredService<LocalChatAdapter>().MandateLanguage.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Local_Adapter_Receives_The_Meetings_Store_And_Its_Export_Only_With_A_Meetings_Database(bool configured)
    {
        var folder = Directory.CreateTempSubdirectory("scout-host-meetings-").FullName;
        try
        {
            using var host = new HostBuilder()
                .ConfigureServices((_, services) => services.AddLogging().AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = "true",
                        ["Scout:Meetings:DbPath"] = configured ? Path.Combine(folder, "meetings.db") : null,
                    })
                    .Build()))
                .Build();
            var meeting = new MeetingRecord(null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Sync", [], []);

            var adapter = host.Services.GetRequiredService<LocalChatAdapter>();

            (adapter.Meetings is SqliteMeetingStore).ShouldBe(configured);
            adapter.Meetings.ShouldBeSameAs(host.Services.GetService<IMeetingStore>());
            (adapter.MeetingRenderer?.Invoke(meeting)).ShouldBe(configured ? MeetingMarkdown.Render(meeting) : null);
            File.Exists(Path.Combine(folder, "meetings.db")).ShouldBeFalse("composing the host opens no database");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_YouTube_The_Local_Adapter_Still_Receives_The_Voice_That_Reads_Replies_Aloud()
    {
        using var host = Build("true");

        var speech = host.Services.GetRequiredService<LocalChatAdapter>().Speech;

        speech.ShouldNotBeNull().ShouldBeSameAs(host.Services.GetRequiredService<ISpeechSynthesizer>());
        (await speech.SynthesizeAsync("Hello.", SpeechChoice.Default, CancellationToken.None)).ShouldBeNull();
    }
}
