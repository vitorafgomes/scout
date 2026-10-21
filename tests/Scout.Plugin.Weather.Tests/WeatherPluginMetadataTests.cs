using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Weather;
using Scout.Plugin.Weather.Abstractions;

namespace Scout.Plugin.Weather.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: the exact snake_case function names plus non-empty
/// descriptions on the functions and their parameters.
/// </summary>
public sealed class WeatherPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IWeatherClient>();
        var plugin = new WeatherPlugin(
            client, TimeProvider.System, Options.Create(new WeatherOptions()));
        return KernelPluginFactory.CreateFromObject(plugin, "Weather");
    }

    [Theory]
    [InlineData("get_current_weather")]
    [InlineData("get_forecast")]
    [InlineData("get_air_quality")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("get_current_weather")]
    [InlineData("get_forecast")]
    [InlineData("get_air_quality")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_current_weather")]
    [InlineData("get_forecast")]
    [InlineData("get_air_quality")]
    public void City_Parameter_Has_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var city = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "city");

        city.ShouldNotBeNull();
        city!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Forecast_Day_Parameter_Has_Description()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_forecast", out var function).ShouldBeTrue();

        var day = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "day");

        day.ShouldNotBeNull();
        day!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Every_Parameter_Has_Description()
    {
        var plugin = BuildPlugin();

        foreach (var function in plugin)
        {
            foreach (var parameter in function.Metadata.Parameters)
            {
                parameter.Description.ShouldNotBeNullOrWhiteSpace();
            }
        }
    }
}
