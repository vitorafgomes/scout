#pragma warning disable SKEXP0001
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Services;
using Microsoft.SemanticKernel.TextToAudio;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureOpenAI;
using Scout.Llm.Providers.OpenAI;

namespace Scout.Llm.Tests;

public sealed class MediaServicesRegistrationTests
{
    private static ServiceProvider Compose(Dictionary<string, string?> settings)
        => new ServiceCollection()
            .AddScoutLlm(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .BuildServiceProvider();

    private static Dictionary<string, string?> OpenAI(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "OpenAI",
            ["Scout:Llm:OpenAI:ApiKey"] = "test-key",
            ["Scout:Llm:OpenAI:Chat:Model"] = "gpt-4o-mini",
            ["Scout:Llm:OpenAI:Embeddings:Model"] = "text-embedding-3-small",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static Dictionary<string, string?> AzureOpenAI(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "AzureOpenAI",
            ["Scout:Llm:AzureOpenAI:ApiKey"] = "test-key",
            ["Scout:Llm:AzureOpenAI:Endpoint"] = "https://my-aoai.openai.azure.com",
            ["Scout:Llm:AzureOpenAI:Chat:Deployment"] = "gpt-4o",
            ["Scout:Llm:AzureOpenAI:Embeddings:Deployment"] = "text-embedding-3-small",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    public static TheoryData<string, Dictionary<string, string?>> ProvidersWithoutMedia() => new()
    {
        {
            "Ollama",
            new()
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
            }
        },
        {
            "Gemini",
            new()
            {
                ["Scout:Llm:Provider"] = "Gemini",
                ["Scout:Llm:Gemini:ApiKey"] = "test-key",
                ["Scout:Llm:Gemini:Chat:Model"] = "gemini-1.5-flash",
                ["Scout:Llm:Gemini:Embeddings:Model"] = "text-embedding-004",
            }
        },
        {
            "ChatGptPlan",
            new()
            {
                ["Scout:Llm:Provider"] = "ChatGptPlan",
                ["Scout:Llm:ChatGptPlan:TokenFile"] = "/nonexistent/scout-chatgpt-token.json",
            }
        },
        {
            "OpenRouter",
            new()
            {
                ["Scout:Llm:Provider"] = "OpenRouter",
                ["Scout:Llm:OpenRouter:ApiKey"] = "test-key",
                ["Scout:Llm:OpenRouter:Chat:Model"] = "openai/gpt-4o-mini",
                ["Scout:Llm:OpenRouter:Embeddings:Model"] = "text-embedding-3-small",
                ["Scout:Llm:OpenRouter:Embeddings:Endpoint"] = "https://api.openai.com/v1",
                ["Scout:Llm:OpenRouter:Embeddings:ApiKey"] = "embeddings-key",
            }
        },
        {
            "Mistral",
            new()
            {
                ["Scout:Llm:Provider"] = "Mistral",
                ["Scout:Llm:Mistral:ApiKey"] = "test-key",
                ["Scout:Llm:Mistral:Chat:Model"] = "mistral-small-latest",
                ["Scout:Llm:Mistral:Embeddings:Model"] = "mistral-embed",
            }
        },
        {
            "HuggingFace",
            new()
            {
                ["Scout:Llm:Provider"] = "HuggingFace",
                ["Scout:Llm:HuggingFace:ApiKey"] = "test-key",
                ["Scout:Llm:HuggingFace:Chat:Model"] = "meta-llama/Llama-3.1-8B-Instruct",
                ["Scout:Llm:HuggingFace:Embeddings:Model"] = "sentence-transformers/all-MiniLM-L6-v2",
            }
        },
        {
            "AzureAIInference",
            new()
            {
                ["Scout:Llm:Provider"] = "AzureAIInference",
                ["Scout:Llm:AzureAIInference:ApiKey"] = "test-key",
                ["Scout:Llm:AzureAIInference:Endpoint"] = "https://my-inference.services.ai.azure.com/models",
                ["Scout:Llm:AzureAIInference:Chat:Model"] = "Phi-3.5-MoE-instruct",
                ["Scout:Llm:AzureAIInference:Embeddings:Model"] = "text-embedding-3-small",
            }
        },
        { "None", new() { ["Scout:Llm:Provider"] = "None" } },
    };

    [Theory]
    [MemberData(nameof(ProvidersWithoutMedia))]
    public void A_Provider_Whose_Connector_Has_No_Speech_Or_Image_Service_Gets_Neither_And_Says_So(
        string name, Dictionary<string, string?> settings)
    {
        using var provider = Compose(settings);
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Services.GetService<ITextToAudioService>().ShouldBeNull();
        kernel.Services.GetService<ITextToImageService>().ShouldBeNull();
        provider.GetRequiredService<MediaCapabilities>().ShouldBe(new MediaCapabilities(false, false, name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAI_Gets_Speech_On_Gpt_4o_Mini_Tts_And_Images_On_Gpt_Image_1_By_Default_With_The_Owners_Key(bool blankKeys)
    {
        using var provider = Compose(blankKeys
            ? OpenAI(("Scout:Llm:OpenAI:SpeechModel", " "), ("Scout:Llm:OpenAI:ImageModel", ""))
            : OpenAI());
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Services.GetRequiredService<ITextToAudioService>().Attributes[AIServiceExtensions.ModelIdKey]
            .ShouldBe("gpt-4o-mini-tts");
        kernel.Services.GetRequiredService<ITextToImageService>().Attributes[AIServiceExtensions.ModelIdKey]
            .ShouldBe("gpt-image-1");
        var media = provider.GetRequiredService<MediaCapabilities>();
        (media.Speech, media.Images, media.Provider).ShouldBe((true, true, "OpenAI"));
    }

    [Fact]
    public void OpenAI_Uses_The_Configured_Speech_And_Image_Models_On_A_Custom_Endpoint_The_Owner_Opened_To_Media()
    {
        using var provider = Compose(OpenAI(
            ("Scout:Llm:OpenAI:SpeechModel", "tts-1-hd"),
            ("Scout:Llm:OpenAI:ImageModel", "gpt-image-1-mini"),
            ("Scout:Llm:OpenAI:Endpoint", "https://proxy.example.test/v1"),
            ("Scout:Llm:OpenAI:MediaOnCustomEndpoint", "true")));
        var kernel = provider.GetRequiredService<Kernel>();

        var speech = kernel.Services.GetRequiredService<ITextToAudioService>().Attributes;
        var images = kernel.Services.GetRequiredService<ITextToImageService>().Attributes;
        (speech[AIServiceExtensions.ModelIdKey], speech[AIServiceExtensions.EndpointKey]).ShouldBe(
            ("tts-1-hd", "https://proxy.example.test/v1"));
        (images[AIServiceExtensions.ModelIdKey], images[AIServiceExtensions.EndpointKey]).ShouldBe(
            ("gpt-image-1-mini", "https://proxy.example.test/v1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void OpenAI_On_A_Custom_Endpoint_Gets_No_Speech_Or_Images_Unless_The_Owner_Opens_It_To_Media(string? mediaOnCustomEndpoint)
    {
        var settings = OpenAI(("Scout:Llm:OpenAI:Endpoint", "https://integrate.example.test/v1"));
        if (mediaOnCustomEndpoint is not null)
        {
            settings["Scout:Llm:OpenAI:MediaOnCustomEndpoint"] = mediaOnCustomEndpoint;
        }

        using var provider = Compose(settings);
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Services.GetService<ITextToAudioService>().ShouldBeNull();
        kernel.Services.GetService<ITextToImageService>().ShouldBeNull();
        provider.GetRequiredService<MediaCapabilities>().ShouldBe(new MediaCapabilities(false, false, "OpenAI"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://API.OpenAI.com")]
    public void OpenAI_On_The_Default_Endpoint_Named_Explicitly_Gets_Speech_And_Images(string endpoint)
    {
        using var provider = Compose(OpenAI(("Scout:Llm:OpenAI:Endpoint", endpoint)));
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Services.GetService<ITextToAudioService>().ShouldNotBeNull();
        kernel.Services.GetService<ITextToImageService>().ShouldNotBeNull();
        var media = provider.GetRequiredService<MediaCapabilities>();
        (media.Speech, media.Images).ShouldBe((true, true));
    }

    [Theory]
    [InlineData(null, "medium")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    public async Task OpenAI_Asks_A_Gpt_Image_Model_For_The_Configured_Quality_Medium_By_Default(string? quality, string sent)
    {
        var handler = ImageHandler();
        var factory = new OpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                OpenAI = new OpenAIOptions { ApiKey = "test-key", Chat = new() { Model = "gpt-4o-mini" }, ImageQuality = quality! },
            }),
            new HttpClient(handler));

        await AskForAPicture(factory, handler);

        var picture = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        var model = picture.GetProperty("model").GetString();
        var asked = picture.TryGetProperty("quality", out var value) ? value.GetString() : null;
        (model, asked).ShouldBe(("gpt-image-1", sent));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("hd")]
    [InlineData("ultra")]
    public void OpenAI_Refuses_An_Image_Quality_Other_Than_Low_Or_Medium(string quality)
    {
        var ex = Should.Throw<ArgumentException>(() => new OpenAILlmProviderFactory(Options.Create(new LlmOptions
        {
            OpenAI = new OpenAIOptions { ApiKey = "test-key", Chat = new() { Model = "gpt-4o-mini" }, ImageQuality = quality },
        })));

        ex.Message.ShouldStartWith($"OpenAIOptions.ImageQuality '{quality}' must be low or medium.");
    }

    [Theory]
    [InlineData(null, "gpt-image-1", "1536x1024", null)]
    [InlineData("dall-e-3", "dall-e-3", "1792x1024", "b64_json")]
    public async Task Azure_OpenAI_Picks_The_Image_Settings_Of_The_Configured_Image_Model(
        string? imageModel, string expectedModel, string size, string? responseFormat)
    {
        var handler = ImageHandler();
        var azure = new AzureOpenAIOptions
        {
            ApiKey = "test-key",
            Endpoint = "https://my-aoai.openai.azure.com",
            Chat = new() { Deployment = "gpt-4o" },
            Embeddings = new() { Deployment = "text-embedding-3-small" },
            ImageDeployment = "image-owner",
        };
        if (imageModel is not null)
        {
            azure.ImageModel = imageModel;
        }

        var factory = new AzureOpenAILlmProviderFactory(Options.Create(new LlmOptions { AzureOpenAI = azure }), new HttpClient(handler));

        await AskForAPicture(factory, handler);

        azure.ImageModel.ShouldBe(expectedModel);
        var picture = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        picture.GetProperty("size").GetString().ShouldBe(size);
        (picture.TryGetProperty("response_format", out var format) ? format.GetString() : null).ShouldBe(responseFormat);
    }

    private static RecordingHandler ImageHandler()
        => new(_ => new StringContent(
            $"{{\"created\":1,\"data\":[{{\"b64_json\":\"{Convert.ToBase64String([0x89, 0x50])}\"}}]}}", Encoding.UTF8, "application/json"));

    private static async Task AskForAPicture(ILlmProviderFactory factory, RecordingHandler handler)
    {
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var image = await builder.Build().Services.GetRequiredService<ITextToImageService>()
            .GetImageContentsAsync(new TextContent("A loaf on a wooden table"), factory.Media.ImageSettings);
        image.ShouldHaveSingleItem().Data!.Value.ToArray().ShouldBe(new byte[] { 0x89, 0x50 });
        handler.Bodies.Count.ShouldBe(1);
    }

    [Fact]
    public void Azure_OpenAI_Without_Speech_Or_Image_Deployments_Gets_Neither()
    {
        using var provider = Compose(AzureOpenAI());
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Services.GetService<ITextToAudioService>().ShouldBeNull();
        kernel.Services.GetService<ITextToImageService>().ShouldBeNull();
        provider.GetRequiredService<MediaCapabilities>().ShouldBe(new MediaCapabilities(false, false, "AzureOpenAI"));
    }

    [Fact]
    public void Azure_OpenAI_Gets_Speech_And_Images_On_Exactly_The_Deployments_The_Owner_Configured()
    {
        using var both = Compose(AzureOpenAI(
            ("Scout:Llm:AzureOpenAI:SpeechDeployment", "tts-owner"),
            ("Scout:Llm:AzureOpenAI:ImageDeployment", "image-owner")));
        using var speechOnly = Compose(AzureOpenAI(("Scout:Llm:AzureOpenAI:SpeechDeployment", "tts-owner")));
        var kernel = both.GetRequiredService<Kernel>();

        kernel.Services.GetRequiredService<ITextToAudioService>().Attributes["DeploymentName"].ShouldBe("tts-owner");
        kernel.Services.GetRequiredService<ITextToImageService>().Attributes["DeploymentName"].ShouldBe("image-owner");
        var media = both.GetRequiredService<MediaCapabilities>();
        (media.Speech, media.Images).ShouldBe((true, true));
        var speechOnlyKernel = speechOnly.GetRequiredService<Kernel>();
        speechOnlyKernel.Services.GetService<ITextToAudioService>().ShouldNotBeNull().Attributes["DeploymentName"].ShouldBe("tts-owner");
        speechOnlyKernel.Services.GetService<ITextToImageService>().ShouldBeNull();
        var partial = speechOnly.GetRequiredService<MediaCapabilities>();
        (partial.Speech, partial.Images).ShouldBe((true, false));
    }

    [Fact]
    public async Task OpenAI_Speech_Asks_For_Mp3_In_A_Named_Voice_And_Images_For_A_Landscape_Picture_As_Bytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/audio/speech", StringComparison.Ordinal)
            ? new ByteArrayContent([1, 2, 3])
            : new StringContent($"{{\"created\":1,\"data\":[{{\"b64_json\":\"{Convert.ToBase64String(png)}\"}}]}}", Encoding.UTF8, "application/json"));
        var factory = new OpenAILlmProviderFactory(
            Options.Create(new LlmOptions { OpenAI = new OpenAIOptions { ApiKey = "test-key", Chat = new() { Model = "gpt-4o-mini" } } }),
            new HttpClient(handler));
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        var kernel = builder.Build();

        var audio = await kernel.Services.GetRequiredService<ITextToAudioService>()
            .GetAudioContentsAsync("Feed the starter daily.", factory.Media.SpeechSettings);
        var image = await kernel.Services.GetRequiredService<ITextToImageService>()
            .GetImageContentsAsync(new TextContent("A loaf on a wooden table"), factory.Media.ImageSettings);

        var speech = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        (speech.GetProperty("model").GetString(), speech.GetProperty("voice").GetString(), speech.GetProperty("response_format").GetString())
            .ShouldBe(("gpt-4o-mini-tts", "alloy", "mp3"));
        audio.ShouldHaveSingleItem().Data!.Value.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        var picture = JsonDocument.Parse(handler.Bodies[1]).RootElement;
        (picture.GetProperty("model").GetString(), picture.GetProperty("size").GetString()).ShouldBe(("gpt-image-1", "1536x1024"));
        picture.TryGetProperty("response_format", out _).ShouldBeFalse();
        image.ShouldHaveSingleItem().Data!.Value.ToArray().ShouldBe(png);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpContent> reply) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = reply(request) };
        }
    }
}
