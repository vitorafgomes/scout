using Microsoft.SemanticKernel;
using Scout.Plugin.WorldClock;
using Scout.Plugin.WorldClock.Tests.Fakes;

namespace Scout.Plugin.WorldClock.Tests;

/// <summary>
/// Pins the Semantic Kernel function metadata the LLM relies on for
/// function-calling: the exact snake_case names, non-empty descriptions on every
/// function and parameter, and the shared teaching phrase that steers the model to
/// call the tool rather than reason about time zones itself. This is the
/// LLM-contract regression net; renaming a function is a breaking change and must
/// fail here.
/// </summary>
public sealed class WorldClockPluginMetadataTests
{
    private const string TeachingPhrase = "Use this instead of guessing time zones.";

    private static KernelPlugin BuildPlugin()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        return KernelPluginFactory.CreateFromObject(
            new WorldClockPlugin(clock, new CityZoneResolver(), "Europe/London"), "WorldClock");
    }

    [Theory]
    [InlineData("current_time_in_city")]
    [InlineData("convert_time_between_cities")]
    [InlineData("time_difference_between_cities")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Three_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            new[]
            {
                "convert_time_between_cities",
                "current_time_in_city",
                "time_difference_between_cities",
            });
    }

    [Fact]
    public void Does_Not_Collide_With_The_Dates_Current_Time_Function()
    {
        var plugin = BuildPlugin();

        // Dates already owns "current_time"; WorldClock must stay distinct so the
        // host never registers two functions under the same name.
        plugin.TryGetFunction("current_time", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("current_time_in_city")]
    [InlineData("convert_time_between_cities")]
    [InlineData("time_difference_between_cities")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("current_time_in_city")]
    [InlineData("convert_time_between_cities")]
    [InlineData("time_difference_between_cities")]
    public void Function_Description_Carries_Teaching_Phrase(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldContain(TeachingPhrase);
    }

    [Theory]
    [InlineData("current_time_in_city")]
    [InlineData("convert_time_between_cities")]
    [InlineData("time_difference_between_cities")]
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
    public void No_Function_Exposes_More_Than_Three_Model_Parameters()
    {
        var plugin = BuildPlugin();

        foreach (var function in plugin)
        {
            function.Metadata.Parameters.Count.ShouldBeLessThanOrEqualTo(3);
        }
    }

    [Fact]
    public void CancellationToken_Is_Not_Exposed_As_A_Parameter()
    {
        var plugin = BuildPlugin();

        foreach (var function in plugin)
        {
            function.Metadata.Parameters.ShouldNotContain(p => p.Name == "ct");
        }
    }
}
