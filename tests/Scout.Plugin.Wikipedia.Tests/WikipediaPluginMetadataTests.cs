using Microsoft.SemanticKernel;
using Scout.Plugin.Wikipedia;
using Scout.Plugin.Wikipedia.Abstractions;

namespace Scout.Plugin.Wikipedia.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable function names plus non-empty descriptions
/// on the functions and their parameters.
/// </summary>
public sealed class WikipediaPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IWikipediaClient>();
        return KernelPluginFactory.CreateFromObject(new WikipediaPlugin(client), "Wikipedia");
    }

    [Theory]
    [InlineData("search_wikipedia")]
    [InlineData("get_article_summary")]
    [InlineData("get_on_this_day")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("search_wikipedia")]
    [InlineData("get_article_summary")]
    [InlineData("get_on_this_day")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("search_wikipedia", "query")]
    [InlineData("search_wikipedia", "language")]
    [InlineData("get_article_summary", "title")]
    [InlineData("get_article_summary", "language")]
    public void Parameter_Has_NonEmpty_Description(string functionName, string parameterName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters
            .SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void OnThisDay_Exposes_No_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_on_this_day", out var function).ShouldBeTrue();

        // The CancellationToken is not surfaced as a model-facing parameter.
        function!.Metadata.Parameters.ShouldBeEmpty();
    }
}
