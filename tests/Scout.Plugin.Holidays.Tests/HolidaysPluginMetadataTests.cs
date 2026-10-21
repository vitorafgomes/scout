using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Scout.Plugin.Holidays;
using Scout.Plugin.Holidays.Abstractions;

namespace Scout.Plugin.Holidays.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class HolidaysPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IHolidaysClient>();
        var clock = new FakeTimeProvider();
        var options = new HolidaysOptions();
        return KernelPluginFactory.CreateFromObject(new HolidaysPlugin(client, clock, options), "Holidays");
    }

    [Theory]
    [InlineData("get_next_holidays")]
    [InlineData("is_holiday")]
    [InlineData("list_holidays")]
    [InlineData("get_long_weekends")]
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
            new[] { "get_long_weekends", "get_next_holidays", "is_holiday", "list_holidays" });
    }

    [Theory]
    [InlineData("get_next_holidays")]
    [InlineData("is_holiday")]
    [InlineData("list_holidays")]
    [InlineData("get_long_weekends")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_next_holidays")]
    [InlineData("is_holiday")]
    [InlineData("list_holidays")]
    [InlineData("get_long_weekends")]
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
    public void IsHoliday_Exposes_Date_And_Country_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("is_holiday", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("date");
        names.ShouldContain("country");
    }

    [Fact]
    public void ListHolidays_Exposes_Country_And_Year_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("list_holidays", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("country");
        names.ShouldContain("year");
    }
}
