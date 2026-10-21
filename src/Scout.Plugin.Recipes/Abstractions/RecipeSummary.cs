namespace Scout.Plugin.Recipes.Abstractions;

/// <summary>
/// A single recipe match from an ingredient search. Minimal read-only projection
/// carrying just the dish name, which is what the model surfaces to the user.
/// </summary>
/// <param name="Name">The dish name, e.g. "Chicken Handi".</param>
public sealed record RecipeSummary(string Name);
