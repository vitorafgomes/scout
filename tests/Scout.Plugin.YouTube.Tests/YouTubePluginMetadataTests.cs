using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies on for
/// function-calling: stable function names plus non-empty descriptions on the functions and
/// their parameters, with the optional ones marked optional.
/// </summary>
public sealed class YouTubePluginMetadataTests
{
    private static readonly KernelPlugin Plugin = KernelPluginFactory.CreateFromObject(
        new YouTubePlugin(Substitute.For<IYouTubeClient>()), "YouTube");

    [Theory]
    [InlineData("youtube_channel_overview")]
    [InlineData("youtube_recent_videos")]
    [InlineData("youtube_video_stats")]
    public void Function_Exists_With_A_Description(string functionName)
    {
        Plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("youtube_channel_overview", "handle", false)]
    [InlineData("youtube_recent_videos", "handle", false)]
    [InlineData("youtube_recent_videos", "count", false)]
    [InlineData("youtube_video_stats", "videos", true)]
    public void Parameter_Is_Described_And_Only_The_Video_List_Is_Required(
        string functionName, string parameterName, bool required)
    {
        var parameter = Plugin[functionName].Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull().Description.ShouldNotBeNullOrWhiteSpace();
        parameter.IsRequired.ShouldBe(required);
    }

    [Fact]
    public void The_Plugin_Exposes_Exactly_The_Three_Read_Functions()
    {
        Plugin.Select(f => f.Name).ShouldBe(
            ["youtube_channel_overview", "youtube_recent_videos", "youtube_video_stats"], ignoreOrder: true);
    }
}
