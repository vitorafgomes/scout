using Microsoft.SemanticKernel;
using Scout.Plugin.WebSearch;
using Scout.Plugin.WebSearch.Abstractions;

namespace Scout.Plugin.WebSearch.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter. This is the LLM-contract
/// regression net.
/// </summary>
public sealed class WebSearchPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IWebSearchClient>();
        var options = new WebSearchOptions { SearxUrl = "https://searx.local" };
        return KernelPluginFactory.CreateFromObject(new WebSearchPlugin(client, options), "WebSearch");
    }

    [Theory]
    [InlineData("search_web")]
    [InlineData("get_instant_answer")]
    [InlineData("fetch_page")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Expected_Functions()
    {
        BuildPlugin().Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[] { "fetch_page", "get_instant_answer", "search_web" });
    }

    [Theory]
    [InlineData("search_web")]
    [InlineData("get_instant_answer")]
    [InlineData("fetch_page")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("search_web")]
    [InlineData("get_instant_answer")]
    [InlineData("fetch_page")]
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
    public void SearchWeb_Exposes_Query_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("search_web", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("query");
    }

    [Fact]
    public void FetchPage_Exposes_Url_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("fetch_page", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("url");
    }
}
