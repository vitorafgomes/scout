using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.CodeReview.Abstractions;
using Scout.Plugin.CodeReview.Tests.Fakes;

namespace Scout.Plugin.CodeReview.Tests;

public sealed class CodeReviewServiceCollectionExtensionsTests
{
    private static IConfiguration Config(string? githubToken)
    {
        var data = new Dictionary<string, string?>();
        if (githubToken is not null)
        {
            data["Scout:Plugins:GitHub:Token"] = githubToken;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static ServiceProvider Build(string? githubToken)
    {
        var services = new ServiceCollection();
        // The reviewer depends on the shared chat-completion service.
        services.AddSingleton<IChatCompletionService>(new CapturingChatCompletionService());
        services.AddCodeReviewPlugin(Config(githubToken));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void No_GitHub_Token_Registers_Plugin_And_Reviewer_But_No_Fetcher()
    {
        var provider = Build(githubToken: null);

        provider.GetService<ICodeReviewer>().ShouldNotBeNull();
        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        provider.GetService<IRepositoryFetcher>().ShouldBeNull();
    }

    [Fact]
    public void Empty_GitHub_Token_Registers_No_Fetcher()
    {
        var provider = Build(githubToken: "");

        provider.GetService<IRepositoryFetcher>().ShouldBeNull();
    }

    [Fact]
    public void Plugin_Is_Named_CodeReview_With_Expected_Functions()
    {
        var provider = Build(githubToken: null);

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("CodeReview");
        plugin.TryGetFunction("analyze_code_quality", out _).ShouldBeTrue();
        plugin.TryGetFunction("review_pull_request", out _).ShouldBeTrue();
        plugin.TryGetFunction("analyze_repository", out _).ShouldBeTrue();
    }

    [Fact]
    public void With_GitHub_Token_Registers_Fetcher()
    {
        var provider = Build(githubToken: "ghp_test");

        provider.GetService<IRepositoryFetcher>().ShouldNotBeNull();
    }

    [Fact]
    public void With_GitHub_Token_Configures_HttpClient_Headers_And_Bearer()
    {
        var provider = Build(githubToken: "ghp_test");

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(IRepositoryFetcher));

        http.BaseAddress.ShouldBe(new Uri("https://api.github.com/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Bearer");
        http.DefaultRequestHeaders.Authorization!.Parameter.ShouldBe("ghp_test");
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("Scout");
        http.DefaultRequestHeaders.Accept.ToString().ShouldContain("application/vnd.github+json");
        http.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version").ShouldContain("2022-11-28");
    }

    [Fact]
    public void Null_Services_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddCodeReviewPlugin(Config(null)));
    }

    [Fact]
    public void Null_Configuration_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddCodeReviewPlugin(null!));
    }
}
