using System.Globalization;
using System.Text;
using Scout.Plugin.WorldClock.Abstractions;

namespace Scout.Plugin.WorldClock;

/// <summary>
/// Offline city-to-IANA resolver backed by a bundled alias table. Input is
/// normalized (trimmed, lower-cased, diacritics stripped, whitespace collapsed)
/// so "São Paulo", "sao paulo", and "SAO  PAULO" all match, and Portuguese city
/// names resolve alongside English ones. A bare city name that maps to more than
/// one country is treated as ambiguous and rejected, steering the caller to the
/// "City, Country" form. No network access.
/// </summary>
public sealed class CityZoneResolver : ICityZoneResolver
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CityZone>> ByCity;
    private static readonly IReadOnlyDictionary<string, CityZone> ByCityCountry;

    static CityZoneResolver()
    {
        var byCity = new Dictionary<string, List<CityZone>>(StringComparer.Ordinal);
        var byCityCountry = new Dictionary<string, CityZone>(StringComparer.Ordinal);

        void Add(string iana, string country, string city, params string[] aliases)
        {
            var zone = new CityZone(city, country, iana);
            var normCity = Normalize(city);
            var normCountry = Normalize(country);

            Register(normCity);
            byCityCountry[$"{normCity}, {normCountry}"] = zone;
            foreach (var alias in aliases)
            {
                var normAlias = Normalize(alias);
                Register(normAlias);
                byCityCountry[$"{normAlias}, {normCountry}"] = zone;
            }

            void Register(string key)
            {
                if (!byCity.TryGetValue(key, out var list))
                {
                    list = [];
                    byCity[key] = list;
                }

                if (!list.Any(z => z.IanaId == zone.IanaId && z.Country == zone.Country))
                {
                    list.Add(zone);
                }
            }
        }

        // Europe.
        Add("Europe/London", "United Kingdom", "London", "londres");
        Add("Europe/London", "United Kingdom", "Manchester");
        Add("Europe/London", "United Kingdom", "Edinburgh");
        Add("Europe/Dublin", "Ireland", "Dublin", "dublim");
        Add("Europe/Paris", "France", "Paris", "paris");
        Add("Europe/Paris", "France", "Lyon");
        Add("Europe/Paris", "France", "Marseille", "marselha");
        Add("Europe/Madrid", "Spain", "Madrid", "madri");
        Add("Europe/Madrid", "Spain", "Barcelona");
        Add("Europe/Lisbon", "Portugal", "Lisbon", "lisboa");
        Add("Europe/Lisbon", "Portugal", "Porto", "oporto");
        Add("Europe/Lisbon", "Portugal", "Coimbra");
        Add("Europe/Lisbon", "Portugal", "Braga");
        Add("Europe/Lisbon", "Portugal", "Faro");
        Add("Europe/Rome", "Italy", "Rome", "roma");
        Add("Europe/Rome", "Italy", "Milan", "milao");
        Add("Europe/Berlin", "Germany", "Berlin", "berlim");
        Add("Europe/Berlin", "Germany", "Munich", "munique");
        Add("Europe/Berlin", "Germany", "Frankfurt");
        Add("Europe/Berlin", "Germany", "Hamburg", "hamburgo");
        Add("Europe/Amsterdam", "Netherlands", "Amsterdam", "amsterda");
        Add("Europe/Brussels", "Belgium", "Brussels", "bruxelas");
        Add("Europe/Zurich", "Switzerland", "Zurich", "zurique");
        Add("Europe/Zurich", "Switzerland", "Geneva", "genebra");
        Add("Europe/Vienna", "Austria", "Vienna", "viena");
        Add("Europe/Prague", "Czechia", "Prague", "praga");
        Add("Europe/Warsaw", "Poland", "Warsaw", "varsovia");
        Add("Europe/Budapest", "Hungary", "Budapest", "budapeste");
        Add("Europe/Stockholm", "Sweden", "Stockholm", "estocolmo");
        Add("Europe/Oslo", "Norway", "Oslo");
        Add("Europe/Copenhagen", "Denmark", "Copenhagen", "copenhague");
        Add("Europe/Helsinki", "Finland", "Helsinki", "helsinque");
        Add("Europe/Athens", "Greece", "Athens", "atenas");
        Add("Europe/Istanbul", "Turkey", "Istanbul", "istambul");
        Add("Europe/Moscow", "Russia", "Moscow", "moscou", "moscovo");
        Add("Europe/Kyiv", "Ukraine", "Kyiv", "kiev");
        Add("Europe/Bucharest", "Romania", "Bucharest", "bucareste");
        Add("Atlantic/Reykjavik", "Iceland", "Reykjavik", "reiquiavique");

        // North America.
        Add("America/New_York", "United States", "New York", "nova york", "nova iorque", "nyc");
        Add("America/New_York", "United States", "Washington", "washington dc");
        Add("America/New_York", "United States", "Boston");
        Add("America/New_York", "United States", "Miami");
        Add("America/New_York", "United States", "Atlanta");
        Add("America/Chicago", "United States", "Chicago");
        Add("America/Chicago", "United States", "Houston");
        Add("America/Chicago", "United States", "Dallas");
        Add("America/Denver", "United States", "Denver");
        Add("America/Phoenix", "United States", "Phoenix");
        Add("America/Los_Angeles", "United States", "Los Angeles", "la");
        Add("America/Los_Angeles", "United States", "San Francisco", "sf");
        Add("America/Los_Angeles", "United States", "Seattle");
        Add("America/Los_Angeles", "United States", "Las Vegas");
        Add("America/Los_Angeles", "United States", "San Jose");
        Add("America/Anchorage", "United States", "Anchorage");
        Add("Pacific/Honolulu", "United States", "Honolulu", "havai");
        Add("America/Toronto", "Canada", "Toronto");
        Add("America/Toronto", "Canada", "Montreal");
        Add("America/Toronto", "Canada", "Ottawa");
        Add("America/Vancouver", "Canada", "Vancouver");
        Add("America/Mexico_City", "Mexico", "Mexico City", "cidade do mexico", "ciudad de mexico");
        Add("America/Costa_Rica", "Costa Rica", "San Jose", "san jose de costa rica");

        // South America.
        Add("America/Sao_Paulo", "Brazil", "São Paulo", "sao paulo", "sp");
        Add("America/Sao_Paulo", "Brazil", "Rio de Janeiro", "rio", "rj");
        Add("America/Sao_Paulo", "Brazil", "Brasília", "brasilia");
        Add("America/Sao_Paulo", "Brazil", "Belo Horizonte", "bh");
        Add("America/Sao_Paulo", "Brazil", "Curitiba");
        Add("America/Sao_Paulo", "Brazil", "Porto Alegre");
        Add("America/Sao_Paulo", "Brazil", "Florianópolis", "florianopolis", "floripa");
        Add("America/Sao_Paulo", "Brazil", "Goiânia", "goiania");
        Add("America/Bahia", "Brazil", "Salvador");
        Add("America/Fortaleza", "Brazil", "Fortaleza");
        Add("America/Fortaleza", "Brazil", "Natal");
        Add("America/Recife", "Brazil", "Recife");
        Add("America/Maceio", "Brazil", "Maceió", "maceio");
        Add("America/Belem", "Brazil", "Belém", "belem");
        Add("America/Manaus", "Brazil", "Manaus");
        Add("America/Cuiaba", "Brazil", "Cuiabá", "cuiaba");
        Add("America/Campo_Grande", "Brazil", "Campo Grande");
        Add("America/Boa_Vista", "Brazil", "Boa Vista");
        Add("America/Rio_Branco", "Brazil", "Rio Branco");
        Add("America/Argentina/Buenos_Aires", "Argentina", "Buenos Aires", "baires");
        Add("America/Santiago", "Chile", "Santiago");
        Add("America/Lima", "Peru", "Lima");
        Add("America/Bogota", "Colombia", "Bogotá", "bogota");
        Add("America/Caracas", "Venezuela", "Caracas");
        Add("America/Montevideo", "Uruguay", "Montevideo", "montevideu");
        Add("America/Asuncion", "Paraguay", "Asunción", "asuncion", "assuncao");
        Add("America/La_Paz", "Bolivia", "La Paz");
        Add("America/Guayaquil", "Ecuador", "Quito");

        // Asia and Middle East.
        Add("Asia/Tokyo", "Japan", "Tokyo", "toquio");
        Add("Asia/Tokyo", "Japan", "Osaka");
        Add("Asia/Seoul", "South Korea", "Seoul", "seul");
        Add("Asia/Shanghai", "China", "Beijing", "pequim");
        Add("Asia/Shanghai", "China", "Shanghai", "xangai");
        Add("Asia/Hong_Kong", "Hong Kong", "Hong Kong", "hong kong");
        Add("Asia/Singapore", "Singapore", "Singapore", "singapura");
        Add("Asia/Bangkok", "Thailand", "Bangkok", "bangcoc");
        Add("Asia/Jakarta", "Indonesia", "Jakarta", "jacarta");
        Add("Asia/Manila", "Philippines", "Manila");
        Add("Asia/Kuala_Lumpur", "Malaysia", "Kuala Lumpur");
        Add("Asia/Kolkata", "India", "Mumbai", "bombaim");
        Add("Asia/Kolkata", "India", "New Delhi", "delhi", "nova delhi");
        Add("Asia/Kolkata", "India", "Bengaluru", "bangalore");
        Add("Asia/Dubai", "United Arab Emirates", "Dubai", "dubai");
        Add("Asia/Dubai", "United Arab Emirates", "Abu Dhabi");
        Add("Asia/Qatar", "Qatar", "Doha");
        Add("Asia/Riyadh", "Saudi Arabia", "Riyadh", "riade");
        Add("Asia/Jerusalem", "Israel", "Jerusalem", "jerusalem");
        Add("Asia/Jerusalem", "Israel", "Tel Aviv", "tel aviv");
        Add("Asia/Tehran", "Iran", "Tehran", "teera");
        Add("Asia/Karachi", "Pakistan", "Karachi");
        Add("Asia/Dhaka", "Bangladesh", "Dhaka");
        Add("Asia/Ho_Chi_Minh", "Vietnam", "Ho Chi Minh City", "saigon");
        Add("Asia/Taipei", "Taiwan", "Taipei", "taipe");

        // Africa.
        Add("Africa/Cairo", "Egypt", "Cairo", "cairo");
        Add("Africa/Johannesburg", "South Africa", "Johannesburg", "joanesburgo");
        Add("Africa/Johannesburg", "South Africa", "Cape Town", "cidade do cabo");
        Add("Africa/Nairobi", "Kenya", "Nairobi");
        Add("Africa/Lagos", "Nigeria", "Lagos");
        Add("Africa/Casablanca", "Morocco", "Casablanca");
        Add("Africa/Accra", "Ghana", "Accra");
        Add("Africa/Addis_Ababa", "Ethiopia", "Addis Ababa");
        Add("Africa/Luanda", "Angola", "Luanda");
        Add("Africa/Maputo", "Mozambique", "Maputo");
        Add("Africa/Tunis", "Tunisia", "Tunis");
        Add("Africa/Algiers", "Algeria", "Algiers", "argel");

        // Oceania.
        Add("Australia/Sydney", "Australia", "Sydney", "sidnei");
        Add("Australia/Melbourne", "Australia", "Melbourne");
        Add("Australia/Brisbane", "Australia", "Brisbane");
        Add("Australia/Perth", "Australia", "Perth");
        Add("Australia/Adelaide", "Australia", "Adelaide");
        Add("Pacific/Auckland", "New Zealand", "Auckland");
        Add("Pacific/Auckland", "New Zealand", "Wellington");

        ByCity = byCity.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<CityZone>)kvp.Value,
            StringComparer.Ordinal);
        ByCityCountry = byCityCountry;
    }

    /// <inheritdoc />
    public bool TryResolve(string? query, out CityZone zone)
    {
        zone = default!;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var normalized = Normalize(query);

        var comma = normalized.IndexOf(',');
        if (comma >= 0)
        {
            var city = normalized[..comma].Trim();
            var country = normalized[(comma + 1)..].Trim();
            if (ByCityCountry.TryGetValue($"{city}, {country}", out var qualified))
            {
                zone = qualified;
                return true;
            }

            // A country was supplied but did not match; fall back to the bare city
            // only when that city is itself unambiguous.
            normalized = city;
        }

        if (ByCity.TryGetValue(normalized, out var matches) && matches.Count == 1)
        {
            zone = matches[0];
            return true;
        }

        return false;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }

        return builder.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }
}
