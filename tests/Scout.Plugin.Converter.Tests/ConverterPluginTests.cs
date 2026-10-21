using Scout.Plugin.Converter;

namespace Scout.Plugin.Converter.Tests;

public sealed class ConverterPluginTests
{
    private static ConverterPlugin Plugin() => new(new ConverterOptions());

    // ---- convert_units: success paths ----------------------------------------

    [Fact]
    public async Task ConvertUnits_Length_Km_To_Mi()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "km", "mi", CancellationToken.None);

        result.ShouldBe("5 km = 3.1068559612 mi");
    }

    [Fact]
    public async Task ConvertUnits_Length_Mi_To_Km()
    {
        var result = await Plugin().ConvertUnitsAsync("1", "mi", "km", CancellationToken.None);

        result.ShouldBe("1 mi = 1.609344 km");
    }

    [Fact]
    public async Task ConvertUnits_Mass_Kg_To_Lb()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "kg", "lb", CancellationToken.None);

        result.ShouldBe("5 kg = 11.0231131092 lb");
    }

    [Fact]
    public async Task ConvertUnits_Temperature_Celsius_To_Fahrenheit()
    {
        var result = await Plugin().ConvertUnitsAsync("100", "°C", "°F", CancellationToken.None);

        result.ShouldBe("100 °C = 212 °F");
    }

    [Fact]
    public async Task ConvertUnits_Accepts_Decimal_And_Negative_Value()
    {
        var result = await Plugin().ConvertUnitsAsync("-2.5", "km", "m", CancellationToken.None);

        result.ShouldBe("-2.5 km = -2500 m");
    }

    [Fact]
    public async Task ConvertUnits_Same_Unit_Is_Identity()
    {
        var result = await Plugin().ConvertUnitsAsync("7", "kg", "kg", CancellationToken.None);

        result.ShouldBe("7 kg = 7 kg");
    }

    [Fact]
    public async Task ConvertUnits_Trims_Whitespace_Around_Units()
    {
        var result = await Plugin().ConvertUnitsAsync(" 5 ", " km ", " m ", CancellationToken.None);

        result.ShouldBe("5 km = 5000 m");
    }

    // ---- convert_units: boundary / corrective paths --------------------------

    [Fact]
    public async Task ConvertUnits_Unknown_From_Unit_Returns_Corrective()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "banana", "mi", CancellationToken.None);

        result.ShouldBe("I don't recognise the unit \"banana\". Try units like km, mi, kg, lb, or °C.");
    }

    [Fact]
    public async Task ConvertUnits_Unknown_To_Unit_Returns_Corrective()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "km", "banana", CancellationToken.None);

        result.ShouldBe("I don't recognise the unit \"banana\". Try units like km, mi, kg, lb, or °C.");
    }

    [Fact]
    public async Task ConvertUnits_Incompatible_Units_Returns_Corrective()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "km", "kg", CancellationToken.None);

        result.ShouldBe("Can't convert km to kg - they measure different things.");
    }

    [Fact]
    public async Task ConvertUnits_NonNumeric_Value_Returns_Teaching()
    {
        var result = await Plugin().ConvertUnitsAsync("five", "km", "mi", CancellationToken.None);

        result.ShouldBe("Provide a numeric value to convert, for example: convert_units value=\"5\" fromUnit=\"km\" toUnit=\"mi\".");
    }

    [Theory]
    [InlineData("", "km", "mi")]
    [InlineData("5", "", "mi")]
    [InlineData("5", "km", "")]
    [InlineData("5", "   ", "mi")]
    public async Task ConvertUnits_Blank_Inputs_Return_Teaching(string value, string from, string to)
    {
        var result = await Plugin().ConvertUnitsAsync(value, from, to, CancellationToken.None);

        result.ShouldBe("Provide a value and two units, for example: convert_units value=\"5\" fromUnit=\"km\" toUnit=\"mi\".");
    }

    [Fact]
    public async Task ConvertUnits_Never_Returns_Empty()
    {
        var result = await Plugin().ConvertUnitsAsync("5", "km", "mi", CancellationToken.None);

        result.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- calculate: success paths --------------------------------------------

    [Fact]
    public async Task Calculate_Simple_Addition()
    {
        var result = await Plugin().CalculateAsync("2 + 3", CancellationToken.None);

        result.ShouldBe("2 + 3 = 5");
    }

    [Fact]
    public async Task Calculate_Respects_Precedence()
    {
        var result = await Plugin().CalculateAsync("2 + 3 * 4", CancellationToken.None);

        result.ShouldBe("2 + 3 * 4 = 14");
    }

    [Fact]
    public async Task Calculate_Respects_Parentheses()
    {
        var result = await Plugin().CalculateAsync("(2 + 3) * 4", CancellationToken.None);

        result.ShouldBe("(2 + 3) * 4 = 20");
    }

    [Fact]
    public async Task Calculate_Handles_Unary_Minus()
    {
        var result = await Plugin().CalculateAsync("10 * -2", CancellationToken.None);

        result.ShouldBe("10 * -2 = -20");
    }

    [Fact]
    public async Task Calculate_Handles_Decimals()
    {
        var result = await Plugin().CalculateAsync("10 / 4", CancellationToken.None);

        result.ShouldBe("10 / 4 = 2.5");
    }

    // ---- calculate: boundary / corrective paths ------------------------------

    [Fact]
    public async Task Calculate_DivideByZero_Returns_Corrective_Without_Throwing()
    {
        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin().CalculateAsync("5 / 0", CancellationToken.None));

        result.ShouldBe("Cannot divide by zero.");
    }

    [Fact]
    public async Task Calculate_DivideByZero_Via_Expression_Returns_Corrective()
    {
        var result = await Plugin().CalculateAsync("8 / (2 - 2)", CancellationToken.None);

        result.ShouldBe("Cannot divide by zero.");
    }

    [Theory]
    [InlineData("(2 + 3")]
    [InlineData("2 + 3)")]
    [InlineData("2 +")]
    [InlineData("* 3")]
    [InlineData("2 3")]
    [InlineData("abc")]
    [InlineData("2 ** 3")]
    public async Task Calculate_Malformed_Returns_Corrective_Without_Throwing(string expression)
    {
        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin().CalculateAsync(expression, CancellationToken.None));

        result.ShouldBe($"I couldn't parse \"{expression.Trim()}\". Use numbers with + - * / and parentheses, e.g. (2 + 3) * 4.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Calculate_Empty_Returns_Teaching(string expression)
    {
        var result = await Plugin().CalculateAsync(expression, CancellationToken.None);

        result.ShouldBe("Provide an expression to calculate, for example: calculate expression=\"(2 + 3) * 4\".");
    }

    [Fact]
    public async Task Calculate_Overlong_Expression_Returns_Teaching()
    {
        var plugin = new ConverterPlugin(new ConverterOptions { MaxExpressionLength = 8 });

        var result = await plugin.CalculateAsync("1 + 2 + 3 + 4 + 5", CancellationToken.None);

        result.ShouldBe("That expression is too long. Keep it under 8 characters.");
    }

    [Fact]
    public async Task Calculate_Never_Returns_Empty()
    {
        var result = await Plugin().CalculateAsync("1 + 1", CancellationToken.None);

        result.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- cancellation: never throws into the kernel --------------------------

    [Fact]
    public async Task ConvertUnits_Cancelled_Returns_Corrective_Without_Throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin().ConvertUnitsAsync("5", "km", "mi", cts.Token));

        result.ShouldBe("The request was cancelled.");
    }

    [Fact]
    public async Task Calculate_Cancelled_Returns_Corrective_Without_Throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin().CalculateAsync("2 + 2", cts.Token));

        result.ShouldBe("The request was cancelled.");
    }

    // ---- construction --------------------------------------------------------

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(() => new ConverterPlugin(null!));
    }
}
