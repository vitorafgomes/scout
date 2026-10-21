using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes;

/// <summary>
/// Read-only recipes plugin exposed to the LLM as Semantic Kernel functions. Each
/// method is a <see cref="KernelFunctionAttribute"/> the model can invoke by name
/// through function-calling. All data comes from GET calls against TheMealDB; no
/// external state is ever changed. Every call returns a non-empty, user-safe
/// string; invalid input yields a corrective sentence and transport or API
/// failures are caught and surfaced as a clean error line, so a tool call never
/// throws back into the kernel. Recipe instructions are untrusted external text:
/// they are length-capped and wrapped in a nonce-delimited fence so a malicious
/// recipe cannot hijack the conversation.
/// </summary>
public sealed class RecipesPlugin
{
    // Cap how many ingredient matches are listed so a common ingredient does not
    // flood the reply.
    private const int MaxIngredientResults = 15;

    // Untrusted-content controls: instructions are capped and fenced with a fresh
    // random nonce so the fenced block cannot be forged by the content itself.
    private const int MaxInstructionChars = 2000;
    private const string FenceOpenPrefix = "<<<UNTRUSTED_CONTENT_";
    private const string FenceCloseSuffix = "_UNTRUSTED_CONTENT>>>";

    private readonly IRecipesClient _client;

    /// <summary>Creates the plugin over a read-only recipes client.</summary>
    /// <param name="client">The recipes client. Must be non-null.</param>
    /// <param name="options">Plugin options. Must be non-null.</param>
    public RecipesPlugin(IRecipesClient client, RecipesOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <summary>Recipes that use a given main ingredient.</summary>
    /// <param name="ingredient">A single main ingredient.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Matching dish names, one per line, or a clean message.</returns>
    [KernelFunction("find_recipes_by_ingredient")]
    [Description("Find recipes that use a given ingredient. The free data source filters by ONE main ingredient only, so pass a single ingredient such as chicken; any extra ingredients are ignored.")]
    public async Task<string> FindRecipesByIngredientAsync(
        [Description("A single main ingredient, e.g. chicken, salmon, or rice.")] string ingredient,
        CancellationToken ct = default)
    {
        var primary = PrimaryIngredient(ingredient);
        if (primary.Length == 0)
        {
            return "Provide a single ingredient, e.g. chicken, salmon, or rice.";
        }

        try
        {
            var matches = await _client.FindByIngredientAsync(primary, ct).ConfigureAwait(false);
            if (matches.Count == 0)
            {
                return $"No recipes found using {primary}. This free source filters by a single ingredient only.";
            }

            return FormatIngredientMatches(primary, matches);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The full recipe for a named dish.</summary>
    /// <param name="dish">The dish name to look up.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>The recipe with ingredients and fenced instructions, or a clean message.</returns>
    [KernelFunction("get_recipe")]
    [Description("Get a full recipe for a named dish, including its category, ingredient list, and cooking instructions.")]
    public async Task<string> GetRecipeAsync(
        [Description("The dish name to look up, e.g. Arrabiata, Beef Wellington, or Feijoada.")] string dish,
        CancellationToken ct = default)
    {
        var query = (dish ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return "Provide a dish name, e.g. Arrabiata, Beef Wellington, or Feijoada.";
        }

        try
        {
            var recipe = await _client.GetRecipeAsync(query, ct).ConfigureAwait(false);
            if (recipe is null)
            {
                return $"No recipe found for {query}.";
            }

            return FormatRecipe(recipe);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>A recipe suggestion, optionally restricted to a category.</summary>
    /// <param name="category">Optional category to restrict the suggestion.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>A recipe with ingredients and fenced instructions, or a clean message.</returns>
    [KernelFunction("suggest_random_recipe")]
    [Description("Suggest a recipe at random, optionally restricted to a category such as Seafood, Dessert, or Vegetarian.")]
    public async Task<string> SuggestRandomRecipeAsync(
        [Description("Optional category to restrict the suggestion, e.g. Seafood, Dessert, or Vegetarian. Leave empty for any category.")] string category = "",
        CancellationToken ct = default)
    {
        var filter = (category ?? string.Empty).Trim();
        try
        {
            var recipe = await _client
                .GetRandomRecipeAsync(filter.Length == 0 ? null : filter, ct)
                .ConfigureAwait(false);
            if (recipe is null)
            {
                return filter.Length == 0
                    ? "Could not fetch a random recipe right now. Try again later."
                    : $"No recipes found in the {filter} category.";
            }

            return FormatRecipe(recipe);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string PrimaryIngredient(string ingredient)
    {
        if (string.IsNullOrWhiteSpace(ingredient))
        {
            return string.Empty;
        }

        // The free source accepts a single ingredient; keep only the first when a
        // comma-separated list is supplied.
        return ingredient.Split(',', 2)[0].Trim();
    }

    private static string FormatIngredientMatches(string ingredient, IReadOnlyList<RecipeSummary> matches)
    {
        var lines = new List<string>
        {
            $"Recipes using {ingredient} (this free source filters by a single ingredient):",
        };

        lines.AddRange(matches.Take(MaxIngredientResults).Select(m => $"- {m.Name}"));

        var hidden = matches.Count - Math.Min(matches.Count, MaxIngredientResults);
        if (hidden > 0)
        {
            lines.Add($"...and {hidden} more.");
        }

        return string.Join("\n", lines);
    }

    private static string FormatRecipe(RecipeDetails recipe)
    {
        var builder = new StringBuilder();

        var meta = string.Join(", ", new[] { recipe.Category, recipe.Area }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        builder.Append(recipe.Name);
        if (meta.Length > 0)
        {
            builder.Append(" - ").Append(meta);
        }

        builder.Append('\n').Append("Ingredients:").Append('\n');
        if (recipe.Ingredients.Count == 0)
        {
            builder.Append("- not listed by the source").Append('\n');
        }
        else
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                builder.Append("- ").Append(ingredient).Append('\n');
            }
        }

        builder.Append("Instructions:").Append('\n');
        builder.Append(FormatInstructions(recipe.Instructions));
        return builder.ToString();
    }

    private static string FormatInstructions(string instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return "Not provided by the source.";
        }

        var trimmed = instructions.Trim();
        var capped = trimmed.Length > MaxInstructionChars
            ? trimmed[..MaxInstructionChars] + " [truncated]"
            : trimmed;

        // A fresh nonce per call: untrusted content cannot predict the markers, so
        // it cannot forge the closing line to escape the fenced region.
        var nonce = Guid.NewGuid().ToString("N");
        return $"{FenceOpenPrefix}{nonce}\n{capped}\n{nonce}{FenceCloseSuffix}";
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is RecipesApiException or HttpRequestException or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        RecipesApiException api => api.Message,
        OperationCanceledException => "The recipes request timed out. Try again later.",
        _ => "Could not reach the recipes service. Try again later.",
    };
}
