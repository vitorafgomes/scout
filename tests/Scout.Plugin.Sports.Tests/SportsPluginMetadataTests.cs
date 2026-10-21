using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Scout.Plugin.Sports;
using Scout.Plugin.Sports.Abstractions;

namespace Scout.Plugin.Sports.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies on
/// for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class SportsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<ISportsClient>();
        var clock = new FakeTimeProvider();
        var options = new SportsOptions();
        return KernelPluginFactory.CreateFromObject(new SportsPlugin(client, clock, options), "Sports");
    }

    [Theory]
    [InlineData("get_last_result")]
    [InlineData("get_next_fixture")]
    [InlineData("get_league_table")]
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
            new[] { "get_last_result", "get_league_table", "get_next_fixture" });
    }

    [Theory]
    [InlineData("get_last_result")]
    [InlineData("get_next_fixture")]
    [InlineData("get_league_table")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_last_result")]
    [InlineData("get_next_fixture")]
    [InlineData("get_league_table")]
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
    public void GetLastResult_Exposes_Team_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_last_result", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("team");
    }

    [Fact]
    public void GetLeagueTable_Exposes_League_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_league_table", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("league");
    }
}
