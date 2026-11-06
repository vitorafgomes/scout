using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubServiceCollectionExtensionsTests
{
    private static IConfiguration Config(string? token)
    {
        var data = new Dictionary<string, string?>();
        if (token is not null)
        {
            data["Scout:Plugins:GitHub:Token"] = token;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static ServiceProvider Build(IConfiguration config)
    {
        var services = new ServiceCollection();
        var gate = new RecordingWriteGate();
        services.AddSingleton<IWriteGate>(gate);
        services.AddSingleton<IApprovedActions>(gate);
        services.AddSingleton(Substitute.For<IOriginNotifier>());
        services.AddGitHubPlugin(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void No_Token_Self_Gates_No_Plugin_No_Client()
    {
        using var provider = Build(Config(token: ""));

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IGitHubClient>().ShouldBeNull();
        provider.GetService<IGitHubWriteClient>().ShouldBeNull();
        provider.GetServices<WriteFunctionDeclaration>().ShouldBeEmpty();
    }

    [Fact]
    public void Missing_Token_Section_Self_Gates()
    {
        using var provider = Build(Config(token: null));

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IGitHubClient>().ShouldBeNull();
    }

    [Fact]
    public void With_Token_Registers_Client_And_Plugin()
    {
        using var provider = Build(Config(token: "test-token"));

        provider.GetServices<KernelPlugin>().Select(p => p.Name).ShouldBe(["GitHub", "GitHubActions"]);
        provider.GetService<IGitHubClient>().ShouldNotBeNull();
        provider.GetService<IGitHubWriteClient>().ShouldNotBeNull();
    }

    [Fact]
    public void With_Token_Registers_GitHub_Named_Plugin()
    {
        using var provider = Build(Config(token: "test-token"));

        var plugin = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHub");
        plugin.TryGetFunction("list_repositories", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_pull_requests", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_issues", out _).ShouldBeTrue();
    }

    [Fact]
    public void With_Token_Configures_HttpClient_Headers()
    {
        using var provider = Build(Config(token: "test-token"));

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IGitHubClient));

        http.BaseAddress.ShouldBe(new Uri("https://api.github.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Bearer");
        http.DefaultRequestHeaders.Authorization!.Parameter.ShouldBe("test-token");
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/vnd.github+json");
        http.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version").ShouldContain("2022-11-28");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddGitHubPlugin(Config("t")));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddGitHubPlugin(null!));
    }

    [Fact]
    public void With_Token_Declares_The_Two_Writes_Into_The_Data_Folder_And_Registers_The_Executor()
    {
        using var provider = Build(Config(token: "test-token"));

        var actions = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions");
        actions.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["create_issue", "submit_pull_request_review"]);
        provider.GetServices<WriteFunctionDeclaration>()
            .Select(d => (d.PluginName, d.FunctionName, d.FeatureId, d.Scope))
            .ShouldBe([
                ("GitHubActions", "submit_pull_request_review", "github", "Scout's data folder"),
                ("GitHubActions", "create_issue", "github", "Scout's data folder"),
            ]);
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ShouldHaveSingleItem().ShouldBeOfType<GitHubActionExecutor>();
        provider.GetRequiredService<GitHubDraftStore>().Directory.ShouldBe(GitHubServiceCollectionExtensions.DefaultDraftsDirectory());
    }

    [Fact]
    public void A_Configured_Drafts_Directory_Is_Used()
    {
        var data = new Dictionary<string, string?>
        {
            ["Scout:Plugins:GitHub:Token"] = "test-token",
            ["Scout:Plugins:GitHub:DraftsDirectory"] = "/tmp/scout-test-drafts",
        };
        using var provider = Build(new ConfigurationBuilder().AddInMemoryCollection(data).Build());

        provider.GetRequiredService<GitHubDraftStore>().Directory.ShouldBe("/tmp/scout-test-drafts");
    }
}
