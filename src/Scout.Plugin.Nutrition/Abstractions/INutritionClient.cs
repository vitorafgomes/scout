namespace Scout.Plugin.Nutrition.Abstractions;

/// <summary>
/// Read-only access to food product data from Open Food Facts. Every operation is
/// an HTTP GET; no external state is ever mutated.
/// </summary>
public interface INutritionClient
{
    /// <summary>
    /// Search Open Food Facts for products matching <paramref name="name"/>,
    /// best matches first. Returns an empty list when nothing matches.
    /// </summary>
    Task<IReadOnlyList<FoodProduct>> SearchProductsAsync(string name, CancellationToken ct);

    /// <summary>
    /// Look up a single product by its <paramref name="barcode"/> (digits only).
    /// Returns null when no product exists for the barcode.
    /// </summary>
    Task<FoodProduct?> GetProductByBarcodeAsync(string barcode, CancellationToken ct);
}
