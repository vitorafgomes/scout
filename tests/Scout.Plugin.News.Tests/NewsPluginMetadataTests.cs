using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.News;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News.Tests;

/// <summary>
/// Verifies the plugin exposes stable Semantic Kernel function metadata the LLM
/// relies on for function-calling: the exact snake_case names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class NewsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var feed = Substitute.For<IFeedClient>();
        var hn = Substitute.For<IHackerNewsClient>();
        var store = Substitute.For<IScoutLocalStore>();
        var clock = new FakeTimeProvider();
        return KernelPluginFactory.CreateFromObject(
            new NewsPlugin(feed, hn, store, clock, new NewsOptions()), "News");
    }

    [Theory]
    [InlineData("get_top_headlines")]
    [InlineData("search_news")]
    [InlineData("get_hacker_news_top")]
    [InlineData("save_to_reading_list")]
    [InlineData("get_reading_list")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Expected_Functions()
    {
        BuildPlugin().Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[]
            {
                "get_hacker_news_top",
                "get_reading_list",
                "get_top_headlines",
                "save_to_reading_list",
                "search_news",
            });
    }

    [Theory]
    [InlineData("get_top_headlines")]
    [InlineData("search_news")]
    [InlineData("get_hacker_news_top")]
    [InlineData("save_to_reading_list")]
    [InlineData("get_reading_list")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_top_headlines", "feed")]
    [InlineData("search_news", "query")]
    [InlineData("save_to_reading_list", "url")]
    [InlineData("save_to_reading_list", "title")]
    public void Parameter_Has_NonEmpty_Description(string functionName, string parameterName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetHackerNewsTop_And_GetReadingList_Take_No_Model_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_hacker_news_top", out var top).ShouldBeTrue();
        plugin.TryGetFunction("get_reading_list", out var list).ShouldBeTrue();

        top!.Metadata.Parameters.ShouldBeEmpty();
        list!.Metadata.Parameters.ShouldBeEmpty();
    }
}
