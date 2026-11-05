#pragma warning disable SKEXP0001
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToAudio;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Llm.Abstractions;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests.Fakes;

/// <summary>Builders for the operator's inputs and canned model answers.</summary>
internal static class Given
{
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static OperatorMandate Mandate(
        IReadOnlyList<string>? pillars = null,
        IReadOnlyList<string>? blocked = null,
        string language = "English")
        => new(
            "Teach home baking",
            "Beginner bakers",
            pillars ?? [],
            "weekly",
            language,
            "warm and precise",
            blocked ?? [],
            "views",
            "10000");

    public static OperatorProjectState Project(string id = "p1", string topic = "sourdough", string status = "researching")
        => new(id, topic, status, "chat", "2026-11-03T12:00:00Z", "2026-11-03T12:00:00Z", [], null, []);

    public static YouTubeChannel Channel()
        => new("UCowner", "Owner Bakes", "@ownerbakes", 1000, 50000, 2, Now.AddYears(-3), "UUowner");

    public static YouTubeVideo Upload(string id = "upload00001", string title = "My first loaf")
        => new(id, title, "Owner Bakes", Now.AddDays(-7), TimeSpan.FromMinutes(8), 1200, 80, 9);

    public static YouTubeSearchResult Found(string id = "found000001", string title = "Sourdough in 10 minutes")
        => new(id, title, "Bread Lab", Now.AddDays(-30), "A fast starter.");

    public static IYouTubeClient YouTube(
        IReadOnlyList<YouTubeVideo>? uploads = null,
        IReadOnlyList<YouTubeSearchResult>? found = null)
    {
        var youtube = Substitute.For<IYouTubeClient>();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>()).Returns(Channel());
        youtube.ListUploadsAsync("UUowner", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(uploads ?? [Upload()]);
        youtube.SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(found ?? [Found()]);
        return youtube;
    }

    public static SearchBudget Budget() => new(new FakeTimeProvider(Now));

    public static string Candidates(params string[] titles)
        => "{\"candidates\":[" + string.Join(",", titles.Select(t => $"{{\"title\":\"{t}\",\"reason\":\"fits\",\"fit\":0.8}}")) + "]}";

    public static string Script(string claims = "[]")
        => "{\"hook\":\"Bread in ten minutes?\",\"sections\":[{\"heading\":\"Starter\",\"narration\":\"Feed it daily.\"}]," +
           "\"callToAction\":\"Subscribe\",\"titleVariants\":[\"A\",\"B\",\"C\"],\"description\":\"How to bake.\"," +
           "\"tags\":[\"bread\"],\"chapters\":[{\"at\":\"0:00\",\"title\":\"Intro\"}],\"claims\":" + claims + "}";

    public static TopicResearcher Researcher(
        IYouTubeClient youtube, ScriptedChat chat, SearchBudget? budget = null, IOperatorControlPlane? controlPlane = null)
        => new(youtube, chat, budget ?? Budget(), controlPlane ?? new RecordingControlPlane());

    public static OperatorPipeline Pipeline(
        IOperatorControlPlane controlPlane,
        IYouTubeClient youtube,
        ScriptedChat chat,
        Scout.Conversation.Abstractions.IOriginNotifier notifier,
        Kernel? media = null,
        TimeProvider? time = null,
        IProcessRunner? runner = null,
        PiperSpeech? piper = null,
        OpenverseHttp? openverse = null,
        Scout.Conversation.Abstractions.IAudioTranscriber? transcriber = null,
        IFaceMouth? faces = null,
        string presenters = "")
    {
        var kernel = media ?? Media();
        var capabilities = Capabilities(kernel);
        var clock = time ?? new SteppingTime(Now);
        var processes = runner ?? new FakeProcessRunner();
        var visuals = new VisualGenerator(kernel, capabilities, clock, openverse is null ? null : new OpenversePictures(openverse.Factory(), clock));
        var narration = new NarrationGenerator(kernel, capabilities, piper ?? Piper(new FakeProcessRunner()));
        return new(
            controlPlane,
            Researcher(youtube, chat, controlPlane: controlPlane),
            new ScriptWriter(chat),
            new ScenePlanner(chat),
            narration,
            visuals,
            Assembly(processes, visuals),
            new TranscriptionStage(Toolset(processes), new MediaProbe(processes), clock, transcriber),
            Presenter(controlPlane, processes, visuals, narration, faces, presenters, clock),
            notifier,
            NullLogger<OperatorPipeline>.Instance);
    }

    /// <summary>A kernel holding only the given speech and image services, as the connected provider would register them.</summary>
    public static Kernel Media(ITextToAudioService? speech = null, ITextToImageService? images = null)
    {
        var builder = Kernel.CreateBuilder();
        if (speech is not null)
        {
            builder.Services.AddSingleton(speech);
        }

        if (images is not null)
        {
            builder.Services.AddSingleton(images);
        }

        return builder.Build();
    }

    public static MediaCapabilities Capabilities(Kernel kernel)
        => new(
            kernel.Services.GetService<ITextToAudioService>() is not null,
            kernel.Services.GetService<ITextToImageService>() is not null,
            "Test")
        {
            SpeechSettings = new PromptExecutionSettings { ModelId = "speech-settings" },
            ImageSettings = new PromptExecutionSettings { ModelId = "image-settings" },
        };

    /// <summary>A speech service answering every narration with <paramref name="bytes"/>, as MP3.</summary>
    public static ITextToAudioService Speech(byte[]? bytes = null)
    {
        var speech = Substitute.For<ITextToAudioService>();
        speech.GetAudioContentsAsync(Arg.Any<string>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<AudioContent>)[new AudioContent(bytes ?? [1, 2, 3], "audio/mpeg")]);
        return speech;
    }

    /// <summary>An image service answering every prompt with <paramref name="bytes"/>, as PNG.</summary>
    public static ITextToImageService Images(byte[]? bytes = null)
    {
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(Arg.Any<TextContent>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<ImageContent>)[new ImageContent(bytes ?? [0x89, 0x50, 0x4E, 0x47], "image/png")]);
        return images;
    }

    public static OperatorScene Scene(
        string id,
        int order,
        string narration = "Feed the starter daily.",
        string status = "planned",
        bool locked = false,
        string audioSource = "none",
        string visualSource = "text_card",
        string? audioFile = null,
        string? visualFile = null,
        int durationMs = 0)
        => new(id, order, narration, $"A picture for {id}", locked, status, null, audioSource, visualSource, audioFile, visualFile, durationMs);

    public static OperatorProjectState Producing(params OperatorScene[] scenes)
        => Project(status: "producing") with
        {
            Script = new OperatorScript(
                "Bread in ten minutes?",
                [new OperatorScriptSection("Starter", "Feed it daily.")],
                "Subscribe",
                ["A", "B", "C"],
                "How to bake.",
                ["bread"],
                []),
            Scenes = scenes,
        };

    /// <summary>The bundled voices at <paramref name="piper"/> and <paramref name="voices"/>; none when both are empty.</summary>
    public static PiperSpeech Piper(IProcessRunner runner, string piper = "", string voices = "", TimeProvider? time = null)
        => new(runner, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { PiperPath = piper, VoicesPath = voices }), time ?? new FakeTimeProvider(Now));

    /// <summary>
    /// A piper program and a voices folder under <paramref name="root"/>: <c>en_GB-cori-medium</c> with one speaker,
    /// <c>en_GB-aru-medium</c> with the speakers <c>03</c> and <c>06</c>, and <c>orphan</c>, a model without its JSON.
    /// </summary>
    public static (string Piper, string Voices) BundledVoices(string root)
    {
        var piper = Path.Combine(root, "piper");
        var voices = Directory.CreateDirectory(Path.Combine(root, "voices")).FullName;
        File.WriteAllText(piper, "#!/bin/sh");
        File.WriteAllBytes(Path.Combine(voices, "en_GB-cori-medium.onnx"), [1]);
        File.WriteAllText(
            Path.Combine(voices, "en_GB-cori-medium.onnx.json"),
            "{\"dataset\":\"cori\",\"audio\":{\"sample_rate\":22050,\"quality\":\"medium\"},\"num_speakers\":1,\"speaker_id_map\":{}}");
        File.WriteAllBytes(Path.Combine(voices, "en_GB-aru-medium.onnx"), [1]);
        File.WriteAllText(
            Path.Combine(voices, "en_GB-aru-medium.onnx.json"),
            "{\"dataset\":\"aru\",\"audio\":{\"sample_rate\":22050,\"quality\":\"medium\"},\"num_speakers\":2,\"speaker_id_map\":{\"03\":0,\"06\":1}}");
        File.WriteAllBytes(Path.Combine(voices, "orphan.onnx"), [1]);
        return (piper, voices);
    }

    /// <summary>The rhubarb program at <paramref name="rhubarb"/> and the mouth shapes in <paramref name="mouths"/>; no lip sync when both are empty.</summary>
    public static RhubarbLipSync LipSync(IProcessRunner runner, string rhubarb = "", string mouths = "", TimeProvider? time = null)
        => new(
            runner,
            Toolset(runner),
            Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { RhubarbPath = rhubarb, MouthsPath = mouths }),
            time ?? new FakeTimeProvider(Now));

    /// <summary>A rhubarb program and a folder of the nine mouth shapes under <paramref name="root"/>, each an empty file.</summary>
    public static (string Rhubarb, string Mouths) MouthShapes(string root)
    {
        var rhubarb = Path.Combine(root, "rhubarb");
        File.WriteAllText(rhubarb, "#!/bin/sh");
        var mouths = Directory.CreateDirectory(Path.Combine(root, "mouths")).FullName;
        foreach (var shape in RhubarbLipSync.Shapes)
        {
            File.WriteAllBytes(Path.Combine(mouths, $"{shape}.png"), []);
        }

        return (rhubarb, mouths);
    }

    /// <summary>The font path the tests configure: a file that exists, so the toolset finds a font.</summary>
    public static readonly string Font = typeof(Given).Assembly.Location;

    public static FfmpegToolset Toolset(IProcessRunner runner, string? font = null, string ffmpeg = "/opt/scout/ffmpeg", TimeProvider? time = null)
        => new(runner, Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { FfmpegPath = ffmpeg, FontPath = font ?? Font }), time ?? new FakeTimeProvider(Now));

    public static FfmpegTools Tools(string encoder = "libopenh264")
        => new("/opt/scout/ffmpeg", "/fonts/NotoSans-Regular.ttf", encoder);

    public static AssemblyStage Assembly(IProcessRunner runner, VisualGenerator visuals)
    {
        var toolset = Toolset(runner);
        return new(toolset, new MediaProbe(runner), Assembler(runner), new ShortsCutter(toolset), new ThumbnailMaker(visuals, toolset));
    }

    /// <summary>The assembler over <paramref name="runner"/>, with lip sync through <paramref name="lipSync"/>, none unless given.</summary>
    public static Assembler Assembler(IProcessRunner runner, RhubarbLipSync? lipSync = null)
    {
        var toolset = Toolset(runner);
        return new(toolset, new MediaProbe(runner), lipSync ?? LipSync(runner));
    }

    /// <summary>The presenter job over <paramref name="runner"/>, finding mouths with <paramref name="faces"/> and the built-in presenters in <paramref name="presenters"/>.</summary>
    public static PresenterStage Presenter(
        IOperatorControlPlane controlPlane,
        IProcessRunner runner,
        VisualGenerator visuals,
        NarrationGenerator narration,
        IFaceMouth? faces = null,
        string presenters = "",
        TimeProvider? time = null,
        RhubarbLipSync? lipSync = null,
        Microsoft.Extensions.Logging.ILogger<PresenterStage>? logger = null)
        => new(
            controlPlane,
            visuals,
            narration,
            new BuiltInPresenters(Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { PresentersPath = presenters })),
            faces ?? new FoundMouth(),
            Assembler(runner, lipSync),
            Toolset(runner),
            new MediaProbe(runner),
            time ?? new FakeTimeProvider(Now),
            logger ?? NullLogger<PresenterStage>.Instance);

    public static string Prompts(int count)
        => "{\"prompts\":[" + string.Join(",", Enumerable.Range(1, count).Select(i => $"\"Picture {i}\"")) + "]}";
}
