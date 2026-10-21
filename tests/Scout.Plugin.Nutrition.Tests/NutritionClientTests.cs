using System.Net;
using Scout.Plugin.Nutrition;
using Scout.Plugin.Nutrition.Tests.Fakes;

namespace Scout.Plugin.Nutrition.Tests;

public sealed class NutritionClientTests
{
    private const string OneProductSearch =
        """
        {"count":1,"products":[{"code":"3017620422003","product_name":"Nutella","brands":"Ferrero, Ferrero France","quantity":"400 g","nutrition_grades":"e","nutriments":{"energy-kcal_100g":539,"fat_100g":30.9,"saturated-fat_100g":10.6,"carbohydrates_100g":57.5,"sugars_100g":56.3,"proteins_100g":6.3,"salt_100g":0.107,"fiber_100g":0}}]}
        """;

    private const string OneProductByBarcode =
        """
        {"status":1,"product":{"code":"3017620422003","product_name":"Nutella","brands":"Ferrero","quantity":"400 g","nutrition_grades":"e","nutriments":{"energy-kcal_100g":539,"proteins_100g":6.3}}}
        """;

    private static (NutritionClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://world.openfoodfacts.org/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new NutritionClient(http), handler);
    }

    [Fact]
    public async Task SearchProducts_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"count":0,"products":[]}""");

        await client.SearchProductsAsync("nutella", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("cgi/search.pl");
        req.RequestUri!.ToString().ShouldContain("search_terms=nutella");
    }

    [Fact]
    public async Task SearchProducts_Parses_Items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, OneProductSearch);

        var products = await client.SearchProductsAsync("nutella", CancellationToken.None);

        products.Count.ShouldBe(1);
        products[0].Name.ShouldBe("Nutella");
        products[0].Barcode.ShouldBe("3017620422003");
        products[0].Brand.ShouldBe("Ferrero");
        products[0].NutritionGrade.ShouldBe("E");
        products[0].Nutrition.EnergyKcal.ShouldBe(539);
        products[0].Nutrition.Fat.ShouldBe(30.9);
        products[0].Nutrition.SaturatedFat.ShouldBe(10.6);
        products[0].Nutrition.Sugars.ShouldBe(56.3);
        products[0].Nutrition.Protein.ShouldBe(6.3);
        products[0].Nutrition.Salt.ShouldBe(0.107);
    }

    [Fact]
    public async Task SearchProducts_Empty_Returns_Empty_List()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"count":0,"products":[]}""");

        var products = await client.SearchProductsAsync("zzzznotathing", CancellationToken.None);

        products.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetByBarcode_Sends_Expected_Path()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, OneProductByBarcode);

        await client.GetProductByBarcodeAsync("3017620422003", CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .ShouldContain("api/v2/product/3017620422003.json");
    }

    [Fact]
    public async Task GetByBarcode_Parses_Product()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, OneProductByBarcode);

        var product = await client.GetProductByBarcodeAsync("3017620422003", CancellationToken.None);

        product.ShouldNotBeNull();
        product!.Name.ShouldBe("Nutella");
        product.Barcode.ShouldBe("3017620422003");
        product.Nutrition.EnergyKcal.ShouldBe(539);
        product.Nutrition.Protein.ShouldBe(6.3);
    }

    [Fact]
    public async Task GetByBarcode_Status_Zero_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"status":0,"status_verbose":"product not found"}""");

        var product = await client.GetProductByBarcodeAsync("0000000000000", CancellationToken.None);

        product.ShouldBeNull();
    }

    [Fact]
    public async Task GetByBarcode_NotFound_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var product = await client.GetProductByBarcodeAsync("0000000000000", CancellationToken.None);

        product.ShouldBeNull();
    }

    [Fact]
    public async Task SearchProducts_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<NutritionApiException>(
            () => client.SearchProductsAsync("nutella", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task GetByBarcode_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");

        var ex = await Should.ThrowAsync<NutritionApiException>(
            () => client.GetProductByBarcodeAsync("3017620422003", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task SearchProducts_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<NutritionApiException>(
            () => client.SearchProductsAsync("nutella", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task SearchProducts_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<NutritionApiException>(
            () => client.SearchProductsAsync("nutella", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task GetByBarcode_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ not json at all");

        var ex = await Should.ThrowAsync<NutritionApiException>(
            () => client.GetProductByBarcodeAsync("3017620422003", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task SearchProducts_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.SearchProductsAsync("nutella", CancellationToken.None));
    }

    [Fact]
    public async Task GetByBarcode_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetProductByBarcodeAsync("3017620422003", CancellationToken.None));
    }
}
