using Microsoft.SemanticKernel;
using Scout.Plugin.Nutrition;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class NutritionPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<INutritionClient>();
        return KernelPluginFactory.CreateFromObject(new NutritionPlugin(client), "Nutrition");
    }

    [Theory]
    [InlineData("find_food_product")]
    [InlineData("get_nutrition_facts")]
    [InlineData("get_nutrition_by_barcode")]
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
            new[] { "find_food_product", "get_nutrition_by_barcode", "get_nutrition_facts" });
    }

    [Theory]
    [InlineData("find_food_product")]
    [InlineData("get_nutrition_facts")]
    [InlineData("get_nutrition_by_barcode")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("find_food_product")]
    [InlineData("get_nutrition_facts")]
    [InlineData("get_nutrition_by_barcode")]
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
    public void FindFoodProduct_Exposes_Name_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("find_food_product", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("name");
    }

    [Fact]
    public void GetNutritionByBarcode_Exposes_Barcode_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_nutrition_by_barcode", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("barcode");
    }
}
