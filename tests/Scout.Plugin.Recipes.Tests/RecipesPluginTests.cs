using Scout.Plugin.Recipes;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes.Tests;

public sealed class RecipesPluginTests
{
    private static IRecipesClient Client() => Substitute.For<IRecipesClient>();

    private static RecipesOptions Options() => new();

    private static RecipesPlugin Plugin(IRecipesClient client) => new(client, Options());

    private static RecipeDetails Details(
        string name = "Spaghetti Carbonara",
        string instructions = "Boil the pasta. Fry the bacon. Combine off the heat.")
        => new(
            name,
            "Pasta",
            "Italian",
            new List<string> { "100g Bacon", "50g Pecorino" },
            instructions);

    [Fact]
    public async Task Find_Formats_List_And_Notes_Single_Ingredient()
    {
        var client = Client();
        client.FindByIngredientAsync("chicken", Arg.Any<CancellationToken>())
            .Returns(new List<RecipeSummary> { new("Chicken Handi"), new("Chicken Congee") });

        var result = await Plugin(client).FindRecipesByIngredientAsync("chicken", CancellationToken.None);

        result.ShouldContain("Chicken Handi");
        result.ShouldContain("Chicken Congee");
        result.ShouldContain("single ingredient");
    }

    [Fact]
    public async Task Find_Uses_First_Ingredient_When_Comma_List()
    {
        var client = Client();
        client.FindByIngredientAsync("chicken", Arg.Any<CancellationToken>())
            .Returns(new List<RecipeSummary> { new("Chicken Handi") });

        var result = await Plugin(client).FindRecipesByIngredientAsync("chicken, rice, garlic", CancellationToken.None);

        result.ShouldContain("Chicken Handi");
        await client.Received(1).FindByIngredientAsync("chicken", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Find_Empty_Returns_Message()
    {
        var client = Client();
        client.FindByIngredientAsync("unobtainium", Arg.Any<CancellationToken>())
            .Returns(new List<RecipeSummary>());

        var result = await Plugin(client).FindRecipesByIngredientAsync("unobtainium", CancellationToken.None);

        result.ShouldBe("No recipes found using unobtainium. This free source filters by a single ingredient only.");
    }

    [Fact]
    public async Task Find_Blank_Returns_Teaching_Without_Calling_Client()
    {
        var client = Client();

        var result = await Plugin(client).FindRecipesByIngredientAsync("   ", CancellationToken.None);

        result.ShouldBe("Provide a single ingredient, e.g. chicken, salmon, or rice.");
        await client.DidNotReceiveWithAnyArgs().FindByIngredientAsync(default!, default);
    }

    [Fact]
    public async Task GetRecipe_Formats_Full_Recipe_With_Fenced_Instructions()
    {
        var client = Client();
        client.GetRecipeAsync("Carbonara", Arg.Any<CancellationToken>())
            .Returns(Details());

        var result = await Plugin(client).GetRecipeAsync("Carbonara", CancellationToken.None);

        result.ShouldContain("Spaghetti Carbonara - Pasta, Italian");
        result.ShouldContain("Ingredients:");
        result.ShouldContain("- 100g Bacon");
        result.ShouldContain("Instructions:");
        result.ShouldContain("UNTRUSTED_CONTENT");
        result.ShouldContain("Boil the pasta.");
    }

    [Fact]
    public async Task GetRecipe_Caps_Long_Instructions()
    {
        var client = Client();
        client.GetRecipeAsync("Long", Arg.Any<CancellationToken>())
            .Returns(Details(instructions: new string('a', 2500)));

        var result = await Plugin(client).GetRecipeAsync("Long", CancellationToken.None);

        result.ShouldContain("[truncated]");
    }

    [Fact]
    public async Task GetRecipe_Null_Returns_Message()
    {
        var client = Client();
        client.GetRecipeAsync("Nope", Arg.Any<CancellationToken>())
            .Returns((RecipeDetails?)null);

        var result = await Plugin(client).GetRecipeAsync("Nope", CancellationToken.None);

        result.ShouldBe("No recipe found for Nope.");
    }

    [Fact]
    public async Task GetRecipe_Blank_Returns_Teaching_Without_Calling_Client()
    {
        var client = Client();

        var result = await Plugin(client).GetRecipeAsync("", CancellationToken.None);

        result.ShouldBe("Provide a dish name, e.g. Arrabiata, Beef Wellington, or Feijoada.");
        await client.DidNotReceiveWithAnyArgs().GetRecipeAsync(default!, default);
    }

    [Fact]
    public async Task SuggestRandom_NoCategory_Passes_Null_And_Formats()
    {
        var client = Client();
        client.GetRandomRecipeAsync(null, Arg.Any<CancellationToken>())
            .Returns(Details("Random Bake"));

        var result = await Plugin(client).SuggestRandomRecipeAsync("", CancellationToken.None);

        result.ShouldContain("Random Bake");
        await client.Received(1).GetRandomRecipeAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuggestRandom_Category_Passes_Trimmed_Category()
    {
        var client = Client();
        client.GetRandomRecipeAsync("Seafood", Arg.Any<CancellationToken>())
            .Returns(Details("Grilled Salmon"));

        var result = await Plugin(client).SuggestRandomRecipeAsync("  Seafood  ", CancellationToken.None);

        result.ShouldContain("Grilled Salmon");
        await client.Received(1).GetRandomRecipeAsync("Seafood", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuggestRandom_Category_Null_Returns_Message()
    {
        var client = Client();
        client.GetRandomRecipeAsync("Nonsense", Arg.Any<CancellationToken>())
            .Returns((RecipeDetails?)null);

        var result = await Plugin(client).SuggestRandomRecipeAsync("Nonsense", CancellationToken.None);

        result.ShouldBe("No recipes found in the Nonsense category.");
    }

    [Fact]
    public async Task SuggestRandom_NoCategory_Null_Returns_Message()
    {
        var client = Client();
        client.GetRandomRecipeAsync(null, Arg.Any<CancellationToken>())
            .Returns((RecipeDetails?)null);

        var result = await Plugin(client).SuggestRandomRecipeAsync("", CancellationToken.None);

        result.ShouldBe("Could not fetch a random recipe right now. Try again later.");
    }

    [Fact]
    public async Task Find_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.FindByIngredientAsync("chicken", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RecipeSummary>>(
                _ => throw new RecipesApiException("The recipes service is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.FindRecipesByIngredientAsync("chicken", CancellationToken.None));

        result.ShouldBe("The recipes service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetRecipe_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetRecipeAsync("Carbonara", Arg.Any<CancellationToken>())
            .Returns<RecipeDetails?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetRecipeAsync("Carbonara", CancellationToken.None));

        result.ShouldBe("The recipes request timed out. Try again later.");
    }

    [Fact]
    public async Task SuggestRandom_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetRandomRecipeAsync(null, Arg.Any<CancellationToken>())
            .Returns<RecipeDetails?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SuggestRandomRecipeAsync("", CancellationToken.None));

        result.ShouldBe("Could not reach the recipes service. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new RecipesPlugin(null!, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new RecipesPlugin(Client(), null!));
    }
}
