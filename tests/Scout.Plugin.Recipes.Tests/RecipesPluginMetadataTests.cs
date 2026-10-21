using Microsoft.SemanticKernel;
using Scout.Plugin.Recipes;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies on
/// for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter.
/// </summary>
public sealed class RecipesPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<IRecipesClient>();
        var options = new RecipesOptions();
        return KernelPluginFactory.CreateFromObject(new RecipesPlugin(client, options), "Recipes");
    }

    [Theory]
    [InlineData("find_recipes_by_ingredient")]
    [InlineData("get_recipe")]
    [InlineData("suggest_random_recipe")]
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
            new[] { "find_recipes_by_ingredient", "get_recipe", "suggest_random_recipe" });
    }

    [Theory]
    [InlineData("find_recipes_by_ingredient")]
    [InlineData("get_recipe")]
    [InlineData("suggest_random_recipe")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("find_recipes_by_ingredient")]
    [InlineData("get_recipe")]
    [InlineData("suggest_random_recipe")]
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
    public void FindRecipesByIngredient_Exposes_Ingredient_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("find_recipes_by_ingredient", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("ingredient");
    }

    [Fact]
    public void GetRecipe_Exposes_Dish_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("get_recipe", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("dish");
    }

    [Fact]
    public void SuggestRandomRecipe_Exposes_Category_Parameter()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("suggest_random_recipe", out var function).ShouldBeTrue();

        function!.Metadata.Parameters.Select(p => p.Name).ShouldContain("category");
    }
}
