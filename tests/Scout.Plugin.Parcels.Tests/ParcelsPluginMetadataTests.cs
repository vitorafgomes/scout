using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Parcels;
using Scout.Plugin.Parcels.Abstractions;
using Scout.Plugin.Parcels.Tests.Fakes;

namespace Scout.Plugin.Parcels.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class ParcelsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IParcelsClient>();
        var store = Substitute.For<IScoutLocalStore>();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var options = new ParcelsOptions();
        return KernelPluginFactory.CreateFromObject(
            new ParcelsPlugin(client, store, clock, options), "Parcels");
    }

    [Theory]
    [InlineData("track_package")]
    [InlineData("watch_package")]
    [InlineData("list_watched_packages")]
    [InlineData("unwatch_package")]
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
            new[] { "list_watched_packages", "track_package", "unwatch_package", "watch_package" });
    }

    [Theory]
    [InlineData("track_package")]
    [InlineData("watch_package")]
    [InlineData("list_watched_packages")]
    [InlineData("unwatch_package")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("track_package")]
    [InlineData("watch_package")]
    [InlineData("unwatch_package")]
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
    public void WatchPackage_Exposes_TrackingNumber_And_Label_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("watch_package", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("trackingNumber");
        names.ShouldContain("label");
    }

    [Fact]
    public void TrackPackage_Exposes_TrackingNumber_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("track_package", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("trackingNumber");
    }
}
