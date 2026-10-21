using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Scout.Contracts;
using Scout.Plugin.SelfInfo;

namespace Scout.Plugin.SelfInfo.Tests;

/// <summary>
/// Verifies the plugin exposes the exact Semantic Kernel function metadata the
/// LLM relies on for function-calling: the five stable snake_case names plus
/// non-empty descriptions on every function and every exposed parameter.
/// </summary>
public sealed class SelfInfoPluginMetadataTests
{
    private static readonly string[] ExpectedFunctions =
    {
        "where_installed",
        "system_info",
        "resource_usage",
        "active_configuration",
        "worker_status",
    };

    private static KernelPlugin BuildPlugin()
    {
        var configuration = new ConfigurationBuilder().Build();
        return KernelPluginFactory.CreateFromObject(
            new SelfInfoPlugin(
                configuration,
                TimeProvider.System,
                NullScoutRequestContext.Instance,
                new SelfInfoOptions()),
            "SelfInfo");
    }

    [Fact]
    public void Exposes_Exactly_The_Five_Expected_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).ShouldBe(ExpectedFunctions, ignoreOrder: true);
    }

    [Theory]
    [InlineData("where_installed")]
    [InlineData("system_info")]
    [InlineData("resource_usage")]
    [InlineData("active_configuration")]
    [InlineData("worker_status")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("where_installed")]
    [InlineData("system_info")]
    [InlineData("resource_usage")]
    [InlineData("active_configuration")]
    [InlineData("worker_status")]
    public void Function_And_Every_Parameter_Have_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();

        foreach (var parameter in function.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
