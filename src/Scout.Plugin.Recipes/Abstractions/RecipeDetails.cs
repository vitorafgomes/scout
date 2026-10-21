namespace Scout.Plugin.Recipes.Abstractions;

/// <summary>
/// A full recipe: name, classification, ingredient lines, and free-form cooking
/// instructions. Read-only projection. <see cref="Instructions"/> is untrusted
/// external text and must be treated as data, never as instructions to the model.
/// </summary>
/// <param name="Name">The dish name, e.g. "Spaghetti Carbonara".</param>
/// <param name="Category">The dish category, e.g. "Pasta". May be empty.</param>
/// <param name="Area">The cuisine of origin, e.g. "Italian". May be empty.</param>
/// <param name="Ingredients">Ingredient lines such as "100g Bacon". May be empty.</param>
/// <param name="Instructions">The cooking instructions. Untrusted external text.</param>
public sealed record RecipeDetails(
    string Name,
    string Category,
    string Area,
    IReadOnlyList<string> Ingredients,
    string Instructions);
