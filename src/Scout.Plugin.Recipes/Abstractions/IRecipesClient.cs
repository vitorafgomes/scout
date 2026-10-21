namespace Scout.Plugin.Recipes.Abstractions;

/// <summary>
/// Read-only access to recipe data. Every operation is a GET; no external state is
/// ever mutated.
/// </summary>
public interface IRecipesClient
{
    /// <summary>
    /// Recipes whose main ingredient is <paramref name="ingredient"/>. The free
    /// data source filters by a single ingredient only.
    /// </summary>
    Task<IReadOnlyList<RecipeSummary>> FindByIngredientAsync(string ingredient, CancellationToken ct);

    /// <summary>
    /// The first full recipe matching <paramref name="dish"/> by name, or
    /// <see langword="null"/> when nothing matches.
    /// </summary>
    Task<RecipeDetails?> GetRecipeAsync(string dish, CancellationToken ct);

    /// <summary>
    /// A single recipe, optionally restricted to <paramref name="category"/>. A
    /// <see langword="null"/> or blank category yields any recipe; a category with
    /// no matches yields <see langword="null"/>.
    /// </summary>
    Task<RecipeDetails?> GetRandomRecipeAsync(string? category, CancellationToken ct);
}
