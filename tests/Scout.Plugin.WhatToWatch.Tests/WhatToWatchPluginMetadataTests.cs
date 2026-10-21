using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Scout.Plugin.WhatToWatch;
using Scout.Plugin.WhatToWatch.Abstractions;

namespace Scout.Plugin.WhatToWatch.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class WhatToWatchPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<ITvMazeClient>();
        var clock = new FakeTimeProvider();
        var options = new WhatToWatchOptions();
        return KernelPluginFactory.CreateFromObject(
            new WhatToWatchPlugin(client, clock, options), "WhatToWatch");
    }

    [Theory]
    [InlineData("search_show")]
    [InlineData("get_next_episode")]
    [InlineData("recommend_shows")]
    [InlineData("where_to_watch")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Expected_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[] { "get_next_episode", "recommend_shows", "search_show", "where_to_watch" });
    }

    [Theory]
    [InlineData("search_show")]
    [InlineData("get_next_episode")]
    [InlineData("recommend_shows")]
    [InlineData("where_to_watch")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("search_show")]
    [InlineData("get_next_episode")]
    [InlineData("recommend_shows")]
    [InlineData("where_to_watch")]
    public void Every_Parameter_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        foreach (var parameter in function!.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void WhereToWatch_Exposes_Title_And_Country_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("where_to_watch", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("title");
        names.ShouldContain("country");
    }
}
