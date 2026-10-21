using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes;

/// <summary>
/// Thin read-only client over TheMealDB REST API using an injected, pre-configured
/// <see cref="HttpClient"/> (base address, timeout, and User-Agent are set by DI).
/// The free public test key is baked into the base address. Non-success responses
/// and unreadable bodies become a user-safe <see cref="RecipesApiException"/>;
/// timeouts and transport faults bubble unchanged. Every call is a GET.
/// </summary>
internal sealed class RecipesClient(HttpClient http) : IRecipesClient
{
    // TheMealDB exposes up to 20 ingredient/measure slots per meal.
    private const int MaxIngredientSlots = 20;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<RecipeSummary>> FindByIngredientAsync(
        string ingredient, CancellationToken ct)
    {
        var envelope = await GetAsync<MealsEnvelope<FilterMealDto>>(
            $"filter.php?i={Uri.EscapeDataString(ingredient)}", ct).ConfigureAwait(false);
        return (envelope.Meals ?? []).Select(MapSummary).ToList();
    }

    public async Task<RecipeDetails?> GetRecipeAsync(string dish, CancellationToken ct)
    {
        var meals = await GetMealsAsync(
            $"search.php?s={Uri.EscapeDataString(dish)}", ct).ConfigureAwait(false);
        return meals.Count == 0 ? null : MapDetails(meals[0]);
    }

    public async Task<RecipeDetails?> GetRandomRecipeAsync(string? category, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            var meals = await GetMealsAsync("random.php", ct).ConfigureAwait(false);
            return meals.Count == 0 ? null : MapDetails(meals[0]);
        }

        var envelope = await GetAsync<MealsEnvelope<FilterMealDto>>(
            $"filter.php?c={Uri.EscapeDataString(category.Trim())}", ct).ConfigureAwait(false);
        var pick = (envelope.Meals ?? []).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.IdMeal));
        if (pick is null)
        {
            return null;
        }

        var full = await GetMealsAsync(
            $"lookup.php?i={Uri.EscapeDataString(pick.IdMeal!)}", ct).ConfigureAwait(false);
        return full.Count == 0 ? null : MapDetails(full[0]);
    }

    private async Task<IReadOnlyList<MealDto>> GetMealsAsync(string path, CancellationToken ct)
    {
        var envelope = await GetAsync<MealsEnvelope<MealDto>>(path, ct).ConfigureAwait(false);
        return envelope.Meals ?? [];
    }

    private static RecipeSummary MapSummary(FilterMealDto dto)
        => new(NonEmpty(dto.StrMeal, "Unknown dish"));

    private static RecipeDetails MapDetails(MealDto dto) => new(
        NonEmpty(dto.StrMeal, "Unknown dish"),
        dto.StrCategory?.Trim() ?? string.Empty,
        dto.StrArea?.Trim() ?? string.Empty,
        ExtractIngredients(dto),
        dto.StrInstructions?.Trim() ?? string.Empty);

    private static IReadOnlyList<string> ExtractIngredients(MealDto dto)
    {
        var lines = new List<string>();
        for (var slot = 1; slot <= MaxIngredientSlots; slot++)
        {
            var ingredient = GetExtraString(dto, $"strIngredient{slot}");
            if (string.IsNullOrWhiteSpace(ingredient))
            {
                continue;
            }

            var measure = GetExtraString(dto, $"strMeasure{slot}");
            lines.Add(string.IsNullOrWhiteSpace(measure)
                ? ingredient.Trim()
                : $"{measure.Trim()} {ingredient.Trim()}");
        }

        return lines;
    }

    private static string? GetExtraString(MealDto dto, string key)
        => dto.Extra is not null
           && dto.Extra.TryGetValue(key, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string NonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// GET that maps non-success responses and unreadable bodies to
    /// <see cref="RecipesApiException"/>. Timeouts and transport faults bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new RecipesApiException(
                "The recipes service returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new RecipesApiException(
                "The recipes service returned an unreadable response.");
        }
    }

    private static RecipesApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.NotFound:
                return new RecipesApiException(
                    "The recipes service endpoint could not be found.", status);
            case HttpStatusCode.TooManyRequests:
                return new RecipesApiException(
                    "The recipes service is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new RecipesApiException(
                        "The recipes service is currently unavailable. Try again later.", status);
                }

                return new RecipesApiException(
                    $"The recipes request failed ({(int)status}).", status);
        }
    }

    private sealed record MealsEnvelope<TMeal>(
        [property: JsonPropertyName("meals")] List<TMeal>? Meals);

    private sealed record FilterMealDto(
        [property: JsonPropertyName("idMeal")] string? IdMeal,
        [property: JsonPropertyName("strMeal")] string? StrMeal);

    private sealed record MealDto(
        [property: JsonPropertyName("idMeal")] string? IdMeal,
        [property: JsonPropertyName("strMeal")] string? StrMeal,
        [property: JsonPropertyName("strCategory")] string? StrCategory,
        [property: JsonPropertyName("strArea")] string? StrArea,
        [property: JsonPropertyName("strInstructions")] string? StrInstructions)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }
}
