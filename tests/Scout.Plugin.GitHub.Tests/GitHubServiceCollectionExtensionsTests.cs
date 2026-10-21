using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub;
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

    [Fact]
    public void No_Token_Self_Gates_No_Plugin_No_Client()
    {
        var services = new ServiceCollection();
        services.AddGitHubPlugin(Config(token: ""));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IGitHubClient>().ShouldBeNull();
    }

    [Fact]
    public void Missing_Token_Section_Self_Gates()
    {
        var services = new ServiceCollection();
        services.AddGitHubPlugin(Config(token: null));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
        provider.GetService<IGitHubClient>().ShouldBeNull();
    }

    [Fact]
    public void With_Token_Registers_Client_And_Plugin()
    {
        var services = new ServiceCollection();
        services.AddGitHubPlugin(Config(token: "test-token"));
        var provider = services.BuildServiceProvider();

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IGitHubClient>().ShouldNotBeNull();
    }

    [Fact]
    public void With_Token_Registers_GitHub_Named_Plugin()
    {
        var services = new ServiceCollection();
        services.AddGitHubPlugin(Config(token: "test-token"));
        var provider = services.BuildServiceProvider();

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("GitHub");
        plugin.TryGetFunction("list_repositories", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_pull_requests", out _).ShouldBeTrue();
        plugin.TryGetFunction("list_issues", out _).ShouldBeTrue();
    }

    [Fact]
    public void With_Token_Configures_HttpClient_Headers()
    {
        var services = new ServiceCollection();
        services.AddGitHubPlugin(Config(token: "test-token"));
        var provider = services.BuildServiceProvider();

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
}
