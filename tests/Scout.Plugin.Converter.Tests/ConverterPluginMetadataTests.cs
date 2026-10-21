using Microsoft.SemanticKernel;
using Scout.Plugin.Converter;

namespace Scout.Plugin.Converter.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter. This is the LLM-contract
/// regression net; renaming a function or dropping a description breaks callers.
/// </summary>
public sealed class ConverterPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
        => KernelPluginFactory.CreateFromObject(new ConverterPlugin(new ConverterOptions()), "Converter");

    [Theory]
    [InlineData("convert_units")]
    [InlineData("calculate")]
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
            new[] { "calculate", "convert_units" });
    }

    [Theory]
    [InlineData("convert_units")]
    [InlineData("calculate")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("convert_units")]
    [InlineData("calculate")]
    public void Every_Parameter_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Parameters.ShouldNotBeEmpty();
        foreach (var parameter in function.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ConvertUnits_Exposes_Value_From_And_To_Parameters()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("convert_units", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("value");
        names.ShouldContain("fromUnit");
        names.ShouldContain("toUnit");
    }

    [Fact]
    public void Calculate_Exposes_Expression_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("calculate", out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("expression");
    }

    [Fact]
    public void Model_Visible_Parameters_Are_All_Strings()
    {
        var plugin = BuildPlugin();

        foreach (var function in plugin)
        {
            foreach (var parameter in function.Metadata.Parameters)
            {
                parameter.ParameterType.ShouldBe(typeof(string));
            }
        }
    }
}
