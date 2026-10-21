using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace Scout.Plugin.Converter;

/// <summary>
/// Offline compute plugin exposed to the LLM as Semantic Kernel functions. It
/// converts between physical units (via UnitsNet) and evaluates decimal
/// arithmetic (via a small hand-written recursive-descent evaluator - never the
/// LLM, never <c>eval</c>, Roslyn, or <c>DataTable.Compute</c>). There is no
/// network and no external state: it is pure, read-only computation. Currency is
/// intentionally excluded; it is owned by the Currency plugin. Every call returns
/// a non-empty, user-safe string; invalid input yields a corrective sentence and
/// a malformed expression or unknown unit is caught and surfaced as a clean line,
/// so a tool call never throws back into the kernel.
/// </summary>
public sealed class ConverterPlugin
{
    private const string UnitHint = "Try units like km, mi, kg, lb, or °C.";

    private readonly int _maxExpressionLength;

    /// <summary>Creates the plugin over its configured limits.</summary>
    /// <param name="options">Plugin options supplying the expression length cap. Must be non-null.</param>
    public ConverterPlugin(ConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxExpressionLength = options.MaxExpressionLength > 0 ? options.MaxExpressionLength : 512;
    }

    /// <summary>Convert a numeric value from one unit to another.</summary>
    /// <param name="value">The numeric value to convert.</param>
    /// <param name="fromUnit">The unit to convert from.</param>
    /// <param name="toUnit">The unit to convert to.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A single "value fromUnit = result toUnit" line, or a corrective sentence.</returns>
    [KernelFunction("convert_units")]
    [Description("Convert a numeric value between physical units, such as length, mass, temperature, volume, or time. Currency is not supported here.")]
    public Task<string> ConvertUnitsAsync(
        [Description("The numeric value to convert, e.g. \"5\" or \"12.5\".")] string value,
        [Description("The unit to convert from, e.g. \"km\", \"kg\", or \"°C\".")] string fromUnit,
        [Description("The unit to convert to, e.g. \"mi\", \"lb\", or \"°F\".")] string toUnit,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ConvertUnits(value, fromUnit, toUnit));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Evaluate a decimal arithmetic expression.</summary>
    /// <param name="expression">The arithmetic expression to evaluate.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A single "expression = result" line, or a corrective sentence.</returns>
    [KernelFunction("calculate")]
    [Description("Evaluate a decimal arithmetic expression with + - * / and parentheses. Numbers only; no variables or functions.")]
    public Task<string> CalculateAsync(
        [Description("The arithmetic expression to evaluate, e.g. \"(2 + 3) * 4\" or \"10 / 4\".")] string expression,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Calculate(expression));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    private static string ConvertUnits(string value, string fromUnit, string toUnit)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.IsNullOrWhiteSpace(fromUnit)
            || string.IsNullOrWhiteSpace(toUnit))
        {
            return "Provide a value and two units, for example: convert_units value=\"5\" fromUnit=\"km\" toUnit=\"mi\".";
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)
            || !double.IsFinite(numeric))
        {
            return "Provide a numeric value to convert, for example: convert_units value=\"5\" fromUnit=\"km\" toUnit=\"mi\".";
        }

        var from = fromUnit.Trim();
        var to = toUnit.Trim();
        if (!UnitConversion.TryConvert(numeric, from, to, out var result, out var error))
        {
            return error switch
            {
                ConversionError.UnknownFromUnit => $"I don't recognise the unit \"{from}\". {UnitHint}",
                ConversionError.UnknownToUnit => $"I don't recognise the unit \"{to}\". {UnitHint}",
                _ => $"Can't convert {from} to {to} - they measure different things.",
            };
        }

        if (!double.IsFinite(result))
        {
            return "That conversion produced an out-of-range number.";
        }

        return $"{FormatNumber(numeric)} {from} = {FormatNumber(result)} {to}";
    }

    private string Calculate(string expression)
    {
        var trimmed = expression?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Provide an expression to calculate, for example: calculate expression=\"(2 + 3) * 4\".";
        }

        if (trimmed.Length > _maxExpressionLength)
        {
            return $"That expression is too long. Keep it under {_maxExpressionLength} characters.";
        }

        try
        {
            var value = ArithmeticEvaluator.Evaluate(trimmed);
            if (!double.IsFinite(value))
            {
                return "That expression doesn't have a finite result.";
            }

            return $"{trimmed} = {FormatNumber(value)}";
        }
        catch (ArithmeticExpressionException ex)
        {
            return ex.Error switch
            {
                ExpressionError.DivideByZero => ex.Message,
                _ => $"I couldn't parse \"{trimmed}\". Use numbers with + - * / and parentheses, e.g. (2 + 3) * 4.",
            };
        }
    }

    private static string FormatNumber(double value)
    {
        var normalized = value == 0 ? 0d : value;
        return normalized.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    // Offline compute never crosses a network boundary; malformed expressions,
    // unknown or incompatible units, and divide-by-zero are all turned into a
    // corrective sentence before they can surface as an exception. The only
    // realistic throw left is cooperative cancellation, which is surfaced as a
    // clean line rather than propagating into the kernel.
    private static bool IsHandledFailure(Exception ex)
        => ex is OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The request was cancelled.",
        _ => "Could not complete the request.",
    };
}
