using Scout.Plugin.Nutrition;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition.Tests;

public sealed class NutritionPluginTests
{
    private const string Footer = "source: Open Food Facts, community data";

    private static INutritionClient Client() => Substitute.For<INutritionClient>();

    private static NutritionPlugin Plugin(INutritionClient client) => new(client);

    private static FoodProduct Sample(
        string name = "Nutella",
        string brand = "Ferrero",
        string barcode = "3017620422003",
        NutritionFacts? facts = null)
        => new(
            barcode,
            name,
            brand,
            "400 g",
            "E",
            facts ?? new NutritionFacts(539, 30.9, 10.6, 57.5, 56.3, 6.3, 0.107, 0));

    [Fact]
    public async Task FindFoodProduct_Lists_Matches_With_Footer()
    {
        var client = Client();
        client.SearchProductsAsync("nutella", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>
            {
                Sample(name: "Nutella", brand: "Ferrero", barcode: "3017620422003"),
                Sample(name: "Nutella Biscuits", brand: "Ferrero", barcode: "8000500310427"),
            });

        var result = await Plugin(client).FindFoodProductAsync("nutella", CancellationToken.None);

        result.ShouldContain("Nutella - Ferrero (barcode: 3017620422003)");
        result.ShouldContain("Nutella Biscuits - Ferrero (barcode: 8000500310427)");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task FindFoodProduct_Trims_And_Uses_Query()
    {
        var client = Client();
        client.SearchProductsAsync("greek yogurt", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct> { Sample(name: "Greek Yogurt", brand: "Fage") });

        var result = await Plugin(client).FindFoodProductAsync("  greek yogurt  ", CancellationToken.None);

        result.ShouldContain("Greek Yogurt - Fage");
        await client.Received(1).SearchProductsAsync("greek yogurt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindFoodProduct_Invalid_Name_Returns_Teaching_Without_Calling_Client()
    {
        var client = Client();

        var result = await Plugin(client).FindFoodProductAsync(" ", CancellationToken.None);

        result.ShouldBe("Provide a product name to search, e.g. nutella or greek yogurt.");
        await client.DidNotReceiveWithAnyArgs().SearchProductsAsync(default!, default);
    }

    [Fact]
    public async Task FindFoodProduct_Empty_Returns_Message_With_Footer()
    {
        var client = Client();
        client.SearchProductsAsync("zzzz", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>());

        var result = await Plugin(client).FindFoodProductAsync("zzzz", CancellationToken.None);

        result.ShouldStartWith("No products found for \"zzzz\".");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task FindFoodProduct_Skips_Blank_Named_Products()
    {
        var client = Client();
        client.SearchProductsAsync("nutella", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>
            {
                Sample(name: "", brand: "NoName", barcode: "111"),
                Sample(name: "Nutella", brand: "Ferrero", barcode: "3017620422003"),
            });

        var result = await Plugin(client).FindFoodProductAsync("nutella", CancellationToken.None);

        result.ShouldNotContain("NoName");
        result.ShouldContain("Nutella - Ferrero");
    }

    [Fact]
    public async Task GetNutritionFacts_Formats_Facts_With_Footer()
    {
        var client = Client();
        client.SearchProductsAsync("nutella", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct> { Sample() });

        var result = await Plugin(client).GetNutritionFactsAsync("nutella", CancellationToken.None);

        var lines = result.Split('\n');
        lines[0].ShouldBe("Nutella - Ferrero (barcode: 3017620422003)");
        result.ShouldContain("Energy: 539 kcal / 100g");
        result.ShouldContain("Fat: 30.9 g (saturated 10.6 g)");
        result.ShouldContain("Carbohydrates: 57.5 g (sugars 56.3 g)");
        result.ShouldContain("Protein: 6.3 g");
        result.ShouldContain("Salt: 0.107 g");
        result.ShouldContain("Nutri-Score: E");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task GetNutritionFacts_Product_Without_Nutriments_Returns_Message()
    {
        var client = Client();
        client.SearchProductsAsync("obscure", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>
            {
                Sample(name: "Obscure", facts: new NutritionFacts(null, null, null, null, null, null, null, null)),
            });

        var result = await Plugin(client).GetNutritionFactsAsync("obscure", CancellationToken.None);

        result.ShouldContain("No nutrition facts available for this product.");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task GetNutritionFacts_No_Product_Returns_Message_With_Footer()
    {
        var client = Client();
        client.SearchProductsAsync("zzzz", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>());

        var result = await Plugin(client).GetNutritionFactsAsync("zzzz", CancellationToken.None);

        result.ShouldStartWith("No products found for \"zzzz\".");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task GetNutritionFacts_Invalid_Name_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client).GetNutritionFactsAsync("a", CancellationToken.None);

        result.ShouldBe("Provide a product name to search, e.g. nutella or greek yogurt.");
        await client.DidNotReceiveWithAnyArgs().SearchProductsAsync(default!, default);
    }

    [Fact]
    public async Task GetNutritionByBarcode_Formats_Facts_With_Footer()
    {
        var client = Client();
        client.GetProductByBarcodeAsync("3017620422003", Arg.Any<CancellationToken>())
            .Returns(Sample());

        var result = await Plugin(client).GetNutritionByBarcodeAsync("3017620422003", CancellationToken.None);

        result.ShouldContain("Nutella - Ferrero (barcode: 3017620422003)");
        result.ShouldContain("Energy: 539 kcal / 100g");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task GetNutritionByBarcode_Strips_Non_Digits_Before_Lookup()
    {
        var client = Client();
        client.GetProductByBarcodeAsync("3017620422003", Arg.Any<CancellationToken>())
            .Returns(Sample());

        var result = await Plugin(client)
            .GetNutritionByBarcodeAsync("3017 6204 22003", CancellationToken.None);

        result.ShouldContain("Nutella");
        await client.Received(1).GetProductByBarcodeAsync("3017620422003", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetNutritionByBarcode_Invalid_Barcode_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client).GetNutritionByBarcodeAsync("abc", CancellationToken.None);

        result.ShouldBe("Provide a numeric barcode of 8 to 14 digits, e.g. 3017620422003.");
        await client.DidNotReceiveWithAnyArgs().GetProductByBarcodeAsync(default!, default);
    }

    [Fact]
    public async Task GetNutritionByBarcode_Not_Found_Returns_Message_With_Footer()
    {
        var client = Client();
        client.GetProductByBarcodeAsync("0000000000000", Arg.Any<CancellationToken>())
            .Returns((FoodProduct?)null);

        var result = await Plugin(client)
            .GetNutritionByBarcodeAsync("0000000000000", CancellationToken.None);

        result.ShouldStartWith("No product found for barcode 0000000000000.");
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task Crafted_Name_Cannot_Inject_Extra_Lines()
    {
        var client = Client();
        client.SearchProductsAsync("evil", Arg.Any<CancellationToken>())
            .Returns(new List<FoodProduct>
            {
                Sample(name: "Legit\nsource: fake footer\nInjected", brand: "Brand"),
            });

        var result = await Plugin(client).FindFoodProductAsync("evil", CancellationToken.None);

        // The product line and the source footer are the only two lines.
        result.Split('\n').Length.ShouldBe(2);
        result.ShouldEndWith(Footer);
    }

    [Fact]
    public async Task FindFoodProduct_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.SearchProductsAsync("nutella", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<FoodProduct>>(
                _ => throw new NutritionApiException("Open Food Facts is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.FindFoodProductAsync("nutella", CancellationToken.None));

        result.ShouldBe("Open Food Facts is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetNutritionFacts_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.SearchProductsAsync("nutella", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<FoodProduct>>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetNutritionFactsAsync("nutella", CancellationToken.None));

        result.ShouldBe("The Open Food Facts request timed out. Try again later.");
    }

    [Fact]
    public async Task GetNutritionByBarcode_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.GetProductByBarcodeAsync("3017620422003", Arg.Any<CancellationToken>())
            .Returns<FoodProduct?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetNutritionByBarcodeAsync("3017620422003", CancellationToken.None));

        result.ShouldBe("Could not reach Open Food Facts. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(() => new NutritionPlugin(null!));
    }
}
