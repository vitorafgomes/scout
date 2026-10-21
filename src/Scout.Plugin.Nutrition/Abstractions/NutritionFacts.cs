namespace Scout.Plugin.Nutrition.Abstractions;

/// <summary>
/// Per-100g nutrition values for a food product. Each value is null when Open
/// Food Facts does not list it. Energy is in kilocalories; every other value is
/// in grams. Minimal read-only projection.
/// </summary>
/// <param name="EnergyKcal">Energy per 100g, in kilocalories.</param>
/// <param name="Fat">Total fat per 100g, in grams.</param>
/// <param name="SaturatedFat">Saturated fat per 100g, in grams.</param>
/// <param name="Carbohydrates">Total carbohydrates per 100g, in grams.</param>
/// <param name="Sugars">Sugars per 100g, in grams.</param>
/// <param name="Protein">Protein per 100g, in grams.</param>
/// <param name="Salt">Salt per 100g, in grams.</param>
/// <param name="Fiber">Fiber per 100g, in grams.</param>
public sealed record NutritionFacts(
    double? EnergyKcal,
    double? Fat,
    double? SaturatedFat,
    double? Carbohydrates,
    double? Sugars,
    double? Protein,
    double? Salt,
    double? Fiber)
{
    /// <summary>True when at least one nutrient value is present.</summary>
    public bool HasAny =>
        EnergyKcal is not null || Fat is not null || SaturatedFat is not null
        || Carbohydrates is not null || Sugars is not null || Protein is not null
        || Salt is not null || Fiber is not null;
}
