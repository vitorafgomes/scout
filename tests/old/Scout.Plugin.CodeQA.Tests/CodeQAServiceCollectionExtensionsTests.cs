using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Core.Llm;
using Scout.Core.Plugins;
using Scout.Plugin.CodeQA.Chunking;
using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Embedding;
using Scout.Plugin.CodeQA.Functions;
using Scout.Plugin.CodeQA.Indexing;
using Scout.Plugin.CodeQA.Retrieval;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests;

/// <summary>
/// Covers <see cref="CodeQAServiceCollectionExtensions"/>. Tests that set
/// <c>SCOUT_GITHUB_TOKEN</c> join the <c>EnvVar</c> collection so they are
/// serialized with the other env-var-mutating tests in the suite.
/// </summary>
[Collection("EnvVar")]
public class CodeQAServiceCollectionExtensionsTests
{
    private const string TokenEnvVar = "SCOUT_GITHUB_TOKEN";

    [Fact]
    public void AddCodeQAPlugin_with_full_configuration_resolves_entire_pipeline()
    {
        var original = Environment.GetEnvironmentVariable(TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, "ghp_dummy-token-not-real");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Plugins:CodeQA:Repository:Owner"] = "scout-org",
                    ["Scout:Plugins:CodeQA:Repository:Name"] = "scout",
                    ["Scout:Plugins:CodeQA:Repository:Branch"] = "main",
                    ["Scout:Plugins:CodeQA:Repository:TokenEnvVar"] = TokenEnvVar,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IEmbedder>());
            services.AddSingleton(Substitute.For<IChatCompletionService>());
            services.AddSingleton(Substitute.For<ILlmProviderFactory>());

            services.AddCodeQAPlugin(configuration);

            using var provider = services.BuildServiceProvider();

            provider.GetService<IVectorStore>().ShouldNotBeNull();
            provider.GetService<IChunker>().ShouldNotBeNull();
            provider.GetService<IGitHubTreeClient>().ShouldNotBeNull();
            provider.GetService<IIngestor>().ShouldNotBeNull();
            provider.GetService<IRetriever>().ShouldNotBeNull();
            provider.GetService<IComposer>().ShouldNotBeNull();
            provider.GetService<CodeQAFunctions>().ShouldNotBeNull();
            provider.GetService<IScoutPlugin>().ShouldNotBeNull();
            provider.GetService<IMessageRouter>().ShouldNotBeNull();
            provider.GetService<IChatCompletionService>().ShouldNotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, original);
        }
    }

    [Fact]
    public void AddCodeQAPlugin_without_Repository_Owner_does_not_register_plugin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        services.AddCodeQAPlugin(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetService<IScoutPlugin>().ShouldBeNull();
        provider.GetService<CodeQAFunctions>().ShouldBeNull();
        provider.GetService<IMessageRouter>().ShouldBeNull();
    }

    [Fact]
    public void AddCodeQAPlugin_binds_CodeQAOptions_from_Scout_Plugins_CodeQA_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:CodeQA:Repository:Owner"] = "scout-org",
                ["Scout:Plugins:CodeQA:Repository:Name"] = "scout",
                ["Scout:Plugins:CodeQA:Repository:Branch"] = "trunk",
                ["Scout:Plugins:CodeQA:Embeddings:Model"] = "nomic-embed-text",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCodeQAPlugin(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CodeQAOptions>>().Value;

        options.Repository.Owner.ShouldBe("scout-org");
        options.Repository.Name.ShouldBe("scout");
        options.Repository.Branch.ShouldBe("trunk");
        options.Embeddings.Model.ShouldBe("nomic-embed-text");
    }

    [Fact]
    public void AddCodeQAPlugin_throws_on_null_services_or_configuration()
    {
        Action nullServices = () =>
            CodeQAServiceCollectionExtensions.AddCodeQAPlugin(
                null!, new ConfigurationBuilder().Build());
        nullServices.ShouldThrow<ArgumentNullException>();

        Action nullConfig = () =>
            new ServiceCollection().AddCodeQAPlugin((IConfiguration)null!);
        nullConfig.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void AddCodeQAPlugin_registers_OctokitGitHubTreeClient_as_default_IGitHubTreeClient()
    {
        var original = Environment.GetEnvironmentVariable(TokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, "ghp_dummy-token-not-real");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Plugins:CodeQA:Repository:Owner"] = "scout-org",
                    ["Scout:Plugins:CodeQA:Repository:Name"] = "scout",
                    ["Scout:Plugins:CodeQA:Repository:TokenEnvVar"] = TokenEnvVar,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IEmbedder>());
            services.AddSingleton(Substitute.For<IChatCompletionService>());
            services.AddSingleton(Substitute.For<ILlmProviderFactory>());

            services.AddCodeQAPlugin(configuration);

            using var provider = services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<IGitHubTreeClient>();

            resolved.ShouldBeOfType<OctokitGitHubTreeClient>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, original);
        }
    }
}
