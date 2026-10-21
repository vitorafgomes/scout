using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition;

/// <summary>
/// Read-only nutrition plugin exposed to the LLM as Semantic Kernel functions.
/// All data comes from Open Food Facts community data via GET calls; no external
/// state is ever changed. Every data reply ends with a source footer. Each method
/// returns a non-empty, user-safe string; invalid model input yields a corrective
/// sentence and transport or API failures are caught and surfaced as a clean
/// error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class NutritionPlugin
{
    private const string SourceFooter = "source: Open Food Facts, community data";

    private const string InvalidNameMessage =
        "Provide a product name to search, e.g. nutella or greek yogurt.";

    private const string InvalidBarcodeMessage =
        "Provide a numeric barcode of 8 to 14 digits, e.g. 3017620422003.";

    private const int MaxListItems = 8;
    private const int MaxFieldLength = 120;

    private readonly INutritionClient _client;

    /// <summary>Creates the plugin over a read-only nutrition client.</summary>
    /// <param name="client">The nutrition client. Must be non-null.</param>
    public NutritionPlugin(INutritionClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Search Open Food Facts for products by name.</summary>
    /// <param name="name">Product or food name to search for.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Matching products, one per line, or a corrective/clean message.</returns>
    [KernelFunction("find_food_product")]
    [Description("Search Open Food Facts for food products by name and list the closest matches with their brand and barcode.")]
    public async Task<string> FindFoodProductAsync(
        [Description("Product or food name to search for, e.g. nutella or greek yogurt.")] string name,
        CancellationToken ct = default)
    {
        if (!IsValidName(name))
        {
            return InvalidNameMessage;
        }

        try
        {
            var products = await _client.SearchProductsAsync(name.Trim(), ct).ConfigureAwait(false);
            var named = products
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Take(MaxListItems)
                .ToList();
            if (named.Count == 0)
            {
                return WithFooter($"No products found for \"{Clean(name)}\".");
            }

            var lines = named.Select(FormatListItem);
            return WithFooter(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Get the nutrition facts for a named product.</summary>
    /// <param name="product">Product or food name to look up.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Per-100g nutrition facts, or a corrective/clean message.</returns>
    [KernelFunction("get_nutrition_facts")]
    [Description("Get the per-100g nutrition facts for a named food product from Open Food Facts, using the closest matching product.")]
    public async Task<string> GetNutritionFactsAsync(
        [Description("Product or food name to look up, e.g. coca cola or nutella.")] string product,
        CancellationToken ct = default)
    {
        if (!IsValidName(product))
        {
            return InvalidNameMessage;
        }

        try
        {
            var products = await _client.SearchProductsAsync(product.Trim(), ct).ConfigureAwait(false);
            var match = products.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name));
            if (match is null)
            {
                return WithFooter($"No products found for \"{Clean(product)}\".");
            }

            return WithFooter(FormatFacts(match));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Look up nutrition facts by barcode.</summary>
    /// <param name="barcode">Product barcode, digits only.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Per-100g nutrition facts, or a corrective/clean message.</returns>
    [KernelFunction("get_nutrition_by_barcode")]
    [Description("Look up a food product by its barcode (EAN/UPC) on Open Food Facts and return its per-100g nutrition facts.")]
    public async Task<string> GetNutritionByBarcodeAsync(
        [Description("Product barcode digits only, e.g. 3017620422003.")] string barcode,
        CancellationToken ct = default)
    {
        if (!TryNormalizeBarcode(barcode, out var code))
        {
            return InvalidBarcodeMessage;
        }

        try
        {
            var product = await _client.GetProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            if (product is null)
            {
                return WithFooter($"No product found for barcode {code}.");
            }

            return WithFooter(FormatFacts(product));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatListItem(FoodProduct product)
        => FormatHeader(product);

    private static string FormatFacts(FoodProduct product)
    {
        var header = FormatHeader(product);
        if (!product.Nutrition.HasAny)
        {
            return $"{header}\nNo nutrition facts available for this product.";
        }

        var facts = product.Nutrition;
        var lines = new List<string> { header };
        if (facts.EnergyKcal is { } energy)
        {
            lines.Add($"Energy: {Fmt(energy)} kcal / 100g");
        }

        if (facts.Fat is { } fat)
        {
            lines.Add(facts.SaturatedFat is { } saturated
                ? $"Fat: {Fmt(fat)} g (saturated {Fmt(saturated)} g)"
                : $"Fat: {Fmt(fat)} g");
        }

        if (facts.Carbohydrates is { } carbohydrates)
        {
            lines.Add(facts.Sugars is { } sugars
                ? $"Carbohydrates: {Fmt(carbohydrates)} g (sugars {Fmt(sugars)} g)"
                : $"Carbohydrates: {Fmt(carbohydrates)} g");
        }

        if (facts.Fiber is { } fiber)
        {
            lines.Add($"Fiber: {Fmt(fiber)} g");
        }

        if (facts.Protein is { } protein)
        {
            lines.Add($"Protein: {Fmt(protein)} g");
        }

        if (facts.Salt is { } salt)
        {
            lines.Add($"Salt: {Fmt(salt)} g");
        }

        if (!string.IsNullOrWhiteSpace(product.NutritionGrade))
        {
            lines.Add($"Nutri-Score: {product.NutritionGrade}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatHeader(FoodProduct product)
    {
        var name = string.IsNullOrWhiteSpace(product.Name) ? "Unknown product" : Clean(product.Name);
        var brand = string.IsNullOrWhiteSpace(product.Brand)
            ? string.Empty
            : $" - {Clean(product.Brand)}";
        var barcode = string.IsNullOrWhiteSpace(product.Barcode)
            ? string.Empty
            : $" (barcode: {Clean(product.Barcode)})";
        return $"{name}{brand}{barcode}";
    }

    private static string WithFooter(string body) => $"{body}\n{SourceFooter}";

    private static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 2;

    private static bool TryNormalizeBarcode(string? barcode, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        if (digits.Length is < 8 or > 14)
        {
            return false;
        }

        normalized = digits;
        return true;
    }

    /// <summary>
    /// Collapse whitespace and control characters and cap the length so a single
    /// crafted product value from community data cannot inject extra output lines
    /// or overflow the reply.
    /// </summary>
    private static string Clean(string value)
    {
        var collapsed = new string(value
            .Select(c => char.IsControl(c) ? ' ' : c)
            .ToArray())
            .Trim();
        while (collapsed.Contains("  ", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return collapsed.Length > MaxFieldLength
            ? collapsed[..MaxFieldLength].TrimEnd() + "..."
            : collapsed;
    }

    private static string Fmt(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsHandledFailure(Exception ex)
        => ex is NutritionApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        NutritionApiException api => api.Message,
        OperationCanceledException => "The Open Food Facts request timed out. Try again later.",
        _ => "Could not reach Open Food Facts. Try again later.",
    };
}
