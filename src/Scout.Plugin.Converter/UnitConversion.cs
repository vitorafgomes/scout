using UnitsNet;

namespace Scout.Plugin.Converter;

/// <summary>
/// Why a unit conversion could not be performed.
/// </summary>
internal enum ConversionError
{
    /// <summary>Conversion succeeded.</summary>
    None,

    /// <summary>The source unit was not recognised.</summary>
    UnknownFromUnit,

    /// <summary>The target unit was not recognised.</summary>
    UnknownToUnit,

    /// <summary>Both units are known but measure different physical quantities.</summary>
    Incompatible,
}

/// <summary>
/// Thin wrapper over UnitsNet that converts a value between two units given only
/// their abbreviations or names (for example "km" to "mi"). Resolution is
/// dimension-agnostic: every registered quantity is probed, and a conversion is
/// only performed when both units belong to the same quantity, which is exactly
/// the compatibility guarantee. Currency is intentionally out of scope; it is
/// owned by the Currency plugin.
/// </summary>
internal static class UnitConversion
{
    /// <summary>
    /// Try to convert <paramref name="value"/> from <paramref name="fromUnit"/> to
    /// <paramref name="toUnit"/>. Returns false with a specific
    /// <paramref name="error"/> when either unit is unknown or the two units are
    /// incompatible.
    /// </summary>
    public static bool TryConvert(
        double value,
        string fromUnit,
        string toUnit,
        out double result,
        out ConversionError error)
    {
        result = 0;

        var fromMatches = ResolveAcrossQuantities(fromUnit);
        var toMatches = ResolveAcrossQuantities(toUnit);

        if (fromMatches.Count == 0)
        {
            error = ConversionError.UnknownFromUnit;
            return false;
        }

        if (toMatches.Count == 0)
        {
            error = ConversionError.UnknownToUnit;
            return false;
        }

        foreach (var (fromType, fromEnum) in fromMatches)
        {
            var target = toMatches.FirstOrDefault(m => m.UnitType == fromType);
            if (target.UnitEnum is null)
            {
                continue;
            }

            result = Quantity.From(value, fromEnum).As(target.UnitEnum);
            error = ConversionError.None;
            return true;
        }

        error = ConversionError.Incompatible;
        return false;
    }

    private static List<(Type UnitType, Enum UnitEnum)> ResolveAcrossQuantities(string unit)
    {
        var matches = new List<(Type, Enum)>();
        if (string.IsNullOrWhiteSpace(unit))
        {
            return matches;
        }

        var token = unit.Trim();
        foreach (var info in Quantity.Infos)
        {
            if (UnitParser.Default.TryParse(token, info.UnitType, out var parsed) && parsed is not null)
            {
                matches.Add((info.UnitType, parsed));
            }
        }

        return matches;
    }
}
