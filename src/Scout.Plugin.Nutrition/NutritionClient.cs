using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition;

/// <summary>
/// Thin read-only client over the Open Food Facts REST API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, timeout, and the
/// required descriptive User-Agent are set by DI). Non-success responses and
/// unreadable bodies become a user-safe <see cref="NutritionApiException"/>;
/// timeouts and transport faults bubble unchanged. Every request is a GET;
/// nothing mutates remote state.
/// </summary>
internal sealed class NutritionClient(HttpClient http) : INutritionClient
{
    private const int SearchPageSize = 12;
    private const string ProductFields =
        "code,product_name,brands,quantity,nutrition_grades,nutriments";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<FoodProduct>> SearchProductsAsync(
        string name, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(name);
        var path =
            $"cgi/search.pl?search_terms={query}&search_simple=1&action=process&json=1"
            + $"&page_size={SearchPageSize}&fields={ProductFields}";

        var dto = await GetAsync<SearchResponseDto>(path, ct).ConfigureAwait(false);
        if (dto?.Products is null || dto.Products.Count == 0)
        {
            return Array.Empty<FoodProduct>();
        }

        return dto.Products.Select(MapProduct).ToList();
    }

    public async Task<FoodProduct?> GetProductByBarcodeAsync(
        string barcode, CancellationToken ct)
    {
        var path = $"api/v2/product/{barcode}.json?fields={ProductFields}";
        var dto = await GetAsync<ProductResponseDto>(path, ct).ConfigureAwait(false);
        if (dto is null || dto.Status != 1 || dto.Product is null)
        {
            return null;
        }

        return MapProduct(dto.Product);
    }

    private static FoodProduct MapProduct(ProductDto dto) => new(
        Barcode: (dto.Code ?? string.Empty).Trim(),
        Name: (dto.ProductName ?? string.Empty).Trim(),
        Brand: FirstBrand(dto.Brands),
        Quantity: (dto.Quantity ?? string.Empty).Trim(),
        NutritionGrade: Grade(dto.NutritionGrades),
        Nutrition: MapNutrition(dto.Nutriments));

    private static string FirstBrand(string? brands)
    {
        if (string.IsNullOrWhiteSpace(brands))
        {
            return string.Empty;
        }

        var parts = brands.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? string.Empty : parts[0];
    }

    private static string Grade(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade))
        {
            return string.Empty;
        }

        var value = grade.Trim();
        return value.Length == 1 && char.IsAsciiLetter(value[0])
            ? value.ToUpperInvariant()
            : string.Empty;
    }

    private static NutritionFacts MapNutrition(
        IReadOnlyDictionary<string, JsonElement>? nutriments) => new(
        EnergyKcal: Number(nutriments, "energy-kcal_100g"),
        Fat: Number(nutriments, "fat_100g"),
        SaturatedFat: Number(nutriments, "saturated-fat_100g"),
        Carbohydrates: Number(nutriments, "carbohydrates_100g"),
        Sugars: Number(nutriments, "sugars_100g"),
        Protein: Number(nutriments, "proteins_100g"),
        Salt: Number(nutriments, "salt_100g"),
        Fiber: Number(nutriments, "fiber_100g"));

    /// <summary>
    /// Read a nutriment defensively: Open Food Facts usually emits numbers but
    /// occasionally strings, so both are accepted and anything else yields null.
    /// </summary>
    private static double? Number(
        IReadOnlyDictionary<string, JsonElement>? nutriments, string key)
    {
        if (nutriments is null || !nutriments.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// GET that returns null on 404 (unknown product/barcode) and maps other
    /// non-success responses and unreadable bodies to
    /// <see cref="NutritionApiException"/>. Transport faults and timeouts bubble
    /// unchanged so the plugin can distinguish them.
    /// </summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new NutritionApiException(
                "Open Food Facts returned an unreadable response. Try again later.");
        }
    }

    private static NutritionApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            return new NutritionApiException(
                "Open Food Facts is rate limiting requests. Try again shortly.", status);
        }

        if ((int)status >= 500)
        {
            return new NutritionApiException(
                "Open Food Facts is currently unavailable. Try again later.", status);
        }

        return new NutritionApiException(
            $"The Open Food Facts request failed ({(int)status}).", status);
    }

    private sealed record SearchResponseDto(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("products")] List<ProductDto>? Products);

    private sealed record ProductResponseDto(
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("product")] ProductDto? Product);

    private sealed record ProductDto(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("product_name")] string? ProductName,
        [property: JsonPropertyName("brands")] string? Brands,
        [property: JsonPropertyName("quantity")] string? Quantity,
        [property: JsonPropertyName("nutrition_grades")] string? NutritionGrades,
        [property: JsonPropertyName("nutriments")] Dictionary<string, JsonElement>? Nutriments);
}
