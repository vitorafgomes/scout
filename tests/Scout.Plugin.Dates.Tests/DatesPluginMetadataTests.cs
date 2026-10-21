using Microsoft.SemanticKernel;
using Scout.Plugin.Dates;
using Scout.Plugin.Dates.Tests.Fakes;

namespace Scout.Plugin.Dates.Tests;

/// <summary>
/// Verifies the plugin exposes the Semantic Kernel function metadata the LLM
/// relies on for function-calling: the exact snake_case names, non-empty
/// descriptions on every function and parameter, and the shared teaching phrase
/// that steers the model to call the tool rather than do date math itself.
/// </summary>
public sealed class DatesPluginMetadataTests
{
    private const string TeachingPhrase = "Use this instead of calculating dates yourself.";

    private static KernelPlugin BuildPlugin()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        return KernelPluginFactory.CreateFromObject(new DatesPlugin(clock, "Europe/London"), "Dates");
    }

    [Theory]
    [InlineData("today")]
    [InlineData("current_time")]
    [InlineData("days_until")]
    [InlineData("day_of_week")]
    [InlineData("date_difference")]
    [InlineData("add_to_date")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Six_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[] { "add_to_date", "current_time", "date_difference", "day_of_week", "days_until", "today" });
    }

    [Theory]
    [InlineData("today")]
    [InlineData("current_time")]
    [InlineData("days_until")]
    [InlineData("day_of_week")]
    [InlineData("date_difference")]
    [InlineData("add_to_date")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("today")]
    [InlineData("days_until")]
    [InlineData("day_of_week")]
    [InlineData("date_difference")]
    [InlineData("add_to_date")]
    public void Function_Description_Carries_Teaching_Phrase(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldContain(TeachingPhrase);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("days_until")]
    [InlineData("day_of_week")]
    [InlineData("date_difference")]
    [InlineData("add_to_date")]
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
    public void CancellationToken_Is_Not_Exposed_As_A_Parameter()
    {
        var plugin = BuildPlugin();

        foreach (var function in plugin)
        {
            function.Metadata.Parameters
                .ShouldNotContain(p => p.Name == "ct");
        }
    }
}
