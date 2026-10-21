using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Core.Plugins;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Functions;
using Scout.Plugin.GitHub.Internal;

namespace Scout.Plugin.GitHub.Tests;

public class GitHubPluginTests
{
    [Fact]
    public void Name_Is_GitHub()
    {
        var plugin = new GitHubPlugin(new ServiceCollection().BuildServiceProvider());
        plugin.Name.ShouldBe("GitHub");
    }

    [Fact]
    public void Capabilities_Are_ReadOnly()
    {
        var plugin = new GitHubPlugin(new ServiceCollection().BuildServiceProvider());
        plugin.Capabilities.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void FeatureId_Is_github()
    {
        var plugin = new GitHubPlugin(new ServiceCollection().BuildServiceProvider());
        plugin.FeatureId.ShouldBe("github");
        plugin.KernelPluginName.ShouldBe("GitHub");
    }

    [Fact]
    public void Build_Exposes_All_Six_Functions_With_NonEmpty_Descriptions()
    {
        var provider = BuildProvider();
        var plugin = new GitHubPlugin(provider);

        var kp = plugin.Build(provider);

        kp.Name.ShouldBe("GitHub");
        kp.FunctionCount.ShouldBe(6);

        kp.Select(f => f.Name).ShouldBe(new[]
        {
            "list_repositories",
            "list_pull_requests",
            "get_pull_request_details",
            "list_issues",
            "get_issue",
            "create_issue",
        }, ignoreOrder: true);

        foreach (var f in kp)
        {
            f.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task BuildAsync_Resolves_From_Held_ServiceProvider()
    {
        var provider = BuildProvider();
        var plugin = new GitHubPlugin(provider);

        var kp = await plugin.BuildAsync(CancellationToken.None);

        kp.FunctionCount.ShouldBe(6);
    }

    [Fact]
    public void Can_Register_In_PluginRegistry_Because_ReadOnly()
    {
        var registry = new PluginRegistry();
        var plugin = new GitHubPlugin(BuildProvider());

        Action act = () => registry.Register(plugin);

        act.ShouldNotThrow();
        registry.Plugins.ShouldHaveSingleItem().Name.ShouldBe("GitHub");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IGitHubClientFactory>());
        services.AddSingleton<IOptions<GitHubPluginOptions>>(
            Options.Create(new GitHubPluginOptions()));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<GitHubFunctions>>(
            NullLogger<GitHubFunctions>.Instance);
        services.AddSingleton(Substitute.For<IFeatureSettingsReader>());
        services.AddSingleton<GitHubFunctions>();
        return services.BuildServiceProvider();
    }
}
