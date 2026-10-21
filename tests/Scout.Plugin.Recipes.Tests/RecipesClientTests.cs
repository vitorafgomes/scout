using System.Net;
using Scout.Plugin.Recipes;
using Scout.Plugin.Recipes.Tests.Fakes;

namespace Scout.Plugin.Recipes.Tests;

public sealed class RecipesClientTests
{
    private const string FullMealJson =
        """
        {"meals":[{"idMeal":"52772","strMeal":"Teriyaki Chicken Casserole","strCategory":"Chicken","strArea":"Japanese","strInstructions":"Preheat oven. Cook chicken.","strIngredient1":"soy sauce","strIngredient2":"water","strIngredient3":"","strMeasure1":"3/4 cup","strMeasure2":"1/2 cup","strMeasure3":" "}]}
        """;

    private static (RecipesClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.themealdb.com/api/json/v1/1/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new RecipesClient(http), handler);
    }

    [Fact]
    public async Task FindByIngredient_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"meals":null}""");

        await client.FindByIngredientAsync("chicken", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("filter.php?i=chicken");
    }

    [Fact]
    public async Task FindByIngredient_Parses_Names()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"meals":[{"strMeal":"Chicken Handi","strMealThumb":"x","idMeal":"52795"},{"strMeal":"Chicken Congee","strMealThumb":"y","idMeal":"52956"}]}""");

        var matches = await client.FindByIngredientAsync("chicken", CancellationToken.None);

        matches.Count.ShouldBe(2);
        matches[0].Name.ShouldBe("Chicken Handi");
        matches[1].Name.ShouldBe("Chicken Congee");
    }

    [Fact]
    public async Task FindByIngredient_Null_Meals_Returns_Empty()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"meals":null}""");

        var matches = await client.FindByIngredientAsync("unobtainium", CancellationToken.None);

        matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRecipe_Parses_Full_Details_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, FullMealJson);

        var recipe = await client.GetRecipeAsync("Teriyaki Chicken", CancellationToken.None);

        recipe.ShouldNotBeNull();
        recipe!.Name.ShouldBe("Teriyaki Chicken Casserole");
        recipe.Category.ShouldBe("Chicken");
        recipe.Area.ShouldBe("Japanese");
        recipe.Instructions.ShouldContain("Preheat oven");
        recipe.Ingredients.Count.ShouldBe(2);
        recipe.Ingredients[0].ShouldBe("3/4 cup soy sauce");
        recipe.Ingredients[1].ShouldBe("1/2 cup water");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("search.php?s=Teriyaki");
    }

    [Fact]
    public async Task GetRecipe_No_Match_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"meals":null}""");

        var recipe = await client.GetRecipeAsync("Nope", CancellationToken.None);

        recipe.ShouldBeNull();
    }

    [Fact]
    public async Task GetRandom_NoCategory_Uses_Random_Endpoint()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, FullMealJson);

        var recipe = await client.GetRandomRecipeAsync(null, CancellationToken.None);

        recipe.ShouldNotBeNull();
        recipe!.Name.ShouldBe("Teriyaki Chicken Casserole");
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("random.php");
    }

    [Fact]
    public async Task GetRandom_Category_Filters_Then_Lookups()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"meals":[{"strMeal":"Grilled Salmon","strMealThumb":"z","idMeal":"52959"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, FullMealJson);

        var recipe = await client.GetRandomRecipeAsync("Seafood", CancellationToken.None);

        recipe.ShouldNotBeNull();
        recipe!.Name.ShouldBe("Teriyaki Chicken Casserole");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.ToString().ShouldContain("filter.php?c=Seafood");
        handler.Requests[1].RequestUri!.ToString().ShouldContain("lookup.php?i=52959");
    }

    [Fact]
    public async Task GetRandom_Category_Empty_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"meals":null}""");

        var recipe = await client.GetRandomRecipeAsync("Nonsense", CancellationToken.None);

        recipe.ShouldBeNull();
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<RecipesApiException>(
            () => client.GetRecipeAsync("Carbonara", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task NotFound_Throws_Api_Exception()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<RecipesApiException>(
            () => client.FindByIngredientAsync("chicken", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("could not be found");
    }

    [Fact]
    public async Task Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<RecipesApiException>(
            () => client.GetRecipeAsync("Carbonara", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_TaskCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetRecipeAsync("Carbonara", CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.FindByIngredientAsync("chicken", CancellationToken.None));
    }
}
