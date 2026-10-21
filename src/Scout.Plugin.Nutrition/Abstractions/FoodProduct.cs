namespace Scout.Plugin.Nutrition.Abstractions;

/// <summary>
/// A single food product from Open Food Facts community data. Minimal read-only
/// projection of the fields Scout surfaces to the user.
/// </summary>
/// <param name="Barcode">The product barcode (EAN/UPC), e.g. "3017620422003"; empty when unknown.</param>
/// <param name="Name">The product name, e.g. "Nutella"; empty when the source omits it.</param>
/// <param name="Brand">The primary brand, e.g. "Ferrero"; empty when unknown.</param>
/// <param name="Quantity">The package quantity, e.g. "400 g"; empty when unknown.</param>
/// <param name="NutritionGrade">The Nutri-Score letter A-E in upper case; empty when unrated.</param>
/// <param name="Nutrition">Per-100g nutrition facts; individual values may be null.</param>
public sealed record FoodProduct(
    string Barcode,
    string Name,
    string Brand,
    string Quantity,
    string NutritionGrade,
    NutritionFacts Nutrition);
