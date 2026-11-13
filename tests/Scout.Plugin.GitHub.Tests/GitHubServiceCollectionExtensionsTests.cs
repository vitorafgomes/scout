using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

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
    public void With_Token_Declares_Each_Write_On_Its_Own_Row_And_Registers_No_Hosted_Service()
    {
        using var provider = Build(Config(token: "test-token"));

        var actions = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions");
        actions.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe([
                "comment_on_issue", "comment_on_pull_request", "create_issue", "get_project_conventions", "get_repository_audit_status",
                "give_pull_request_verdict", "pause_repository_audit", "remember_project_conventions", "reply_to_review_comment", "run_pull_request_tests",
                "snapshot_repository", "start_repository_audit", "stop_repository_audit", "submit_pull_request_review",
            ]);
        provider.GetServices<WriteFunctionDeclaration>()
            .Select(d => (d.PluginName, d.FunctionName, d.FeatureId, d.Scope))
            .ShouldBe([
                ("GitHubActions", "submit_pull_request_review", "github", "Pull request reviews"),
                ("GitHubActions", "create_issue", "github", "New issues"),
                ("GitHubActions", "give_pull_request_verdict", "github", "Pull request verdicts"),
                ("GitHubActions", "comment_on_pull_request", "github", "Pull request comments"),
                ("GitHubActions", "reply_to_review_comment", "github", "Pull request comments"),
                ("GitHubActions", "comment_on_issue", "github", "Issue comments"),
                ("GitHubActions", "remember_project_conventions", "github", "Scout's data folder"),
                ("GitHubActions", "snapshot_repository", "github", "Scout's data folder"),
                ("GitHubActions", "start_repository_audit", "github", "Scout's data folder"),
                ("GitHubActions", "stop_repository_audit", "github", "Scout's data folder"),
                ("GitHubActions", "pause_repository_audit", "github", "Scout's data folder"),
                ("GitHubActions", "run_pull_request_tests", "github", "Local container"),
            ]);
        provider.GetRequiredService<RepositorySnapshotStore>().Directory.ShouldBe(GitHubServiceCollectionExtensions.DefaultSnapshotsDirectory());
        provider.GetRequiredService<RepositorySnapshotStore>().MaxBytes.ShouldBe(RepositorySnapshotStore.DefaultMaxBytes);
        provider.GetRequiredService<GitHubProfileStore>().Directory.ShouldBe(GitHubServiceCollectionExtensions.DefaultProfilesDirectory());
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ShouldBeEmpty();
        provider.GetRequiredService<IRepositorySnapshots>().ShouldBeSameAs(provider.GetRequiredService<RepositorySnapshotStore>());
    }

    [Fact]
    public void A_Configured_Profiles_Directory_Is_Used()
    {
        var data = new Dictionary<string, string?>
        {
            ["Scout:Plugins:GitHub:Token"] = "test-token",
            ["Scout:Plugins:GitHub:ProfilesDirectory"] = "/tmp/scout-test-profiles",
            ["Scout:Plugins:GitHub:SnapshotsDirectory"] = "/tmp/scout-test-snapshots",
            ["Scout:Plugins:GitHub:SnapshotMaxBytes"] = "1024",
        };
        using var provider = Build(new ConfigurationBuilder().AddInMemoryCollection(data).Build());

        provider.GetRequiredService<GitHubProfileStore>().Directory.ShouldBe("/tmp/scout-test-profiles");
        provider.GetRequiredService<RepositorySnapshotStore>().Directory.ShouldBe("/tmp/scout-test-snapshots");
        provider.GetRequiredService<RepositorySnapshotStore>().MaxBytes.ShouldBe(1024);
    }

    [Fact]
    public void The_Owner_Sees_Which_Pull_Request_A_Verdict_Or_A_Comment_Acts_On_And_Which_Verdict_Before_Approving_It_And_A_Verdict_An_Issue_Or_A_Comment_Keeps_What_It_Needs_To_Run()
    {
        using var provider = Build(Config(token: "test-token"));

        var shown = provider.GetServices<WriteFunctionDeclaration>().ToDictionary(d => d.FunctionName, d => d.SummaryArguments);

        shown["give_pull_request_verdict"].ShouldBe(["owner", "repo", "number", "verdict"]);
        shown["comment_on_pull_request"].ShouldBe(["owner", "repo", "number"]);
        shown["comment_on_issue"].ShouldBe(["owner", "repo", "number"]);
        shown["reply_to_review_comment"].ShouldBe(["owner", "repo", "number", "comment_id"]);
        provider.GetServices<WriteFunctionDeclaration>().Where(d => d.CarriesPayload).Select(d => d.FunctionName).ShouldBe(["submit_pull_request_review", "create_issue", "give_pull_request_verdict", "comment_on_pull_request", "reply_to_review_comment", "comment_on_issue"]);
    }
}
