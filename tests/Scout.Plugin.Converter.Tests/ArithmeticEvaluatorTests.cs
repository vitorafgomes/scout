using Scout.Plugin.Converter;

namespace Scout.Plugin.Converter.Tests;

/// <summary>
/// Direct unit tests for the hand-written recursive-descent evaluator (internal,
/// visible via InternalsVisibleTo). Proves the arithmetic contract and the
/// boundary failure modes: unbalanced parentheses, divide-by-zero, empty, and
/// trailing operators - each a typed exception the plugin turns into a corrective
/// sentence.
/// </summary>
public sealed class ArithmeticEvaluatorTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("2 + 3", 5)]
    [InlineData("10 - 4", 6)]
    [InlineData("6 * 7", 42)]
    [InlineData("20 / 5", 4)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2 * (3 + 4) - 1", 13)]
    [InlineData("-5 + 2", -3)]
    [InlineData("10 * -2", -20)]
    [InlineData("--5", 5)]
    [InlineData("+7", 7)]
    [InlineData("1.5 + 2.5", 4)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("3.14 * 2", 6.28)]
    [InlineData("((1 + 2) * (3 + 4))", 21)]
    [InlineData("  7  +  8  ", 15)]
    public void Evaluate_Returns_Expected(string expression, double expected)
    {
        var result = ArithmeticEvaluator.Evaluate(expression);

        result.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Evaluate_Division_Is_Left_Associative()
    {
        // (100 / 10) / 2 = 5, not 100 / (10 / 2) = 20.
        ArithmeticEvaluator.Evaluate("100 / 10 / 2").ShouldBe(5, 1e-9);
    }

    [Theory]
    [InlineData("5 / 0")]
    [InlineData("1 / (3 - 3)")]
    [InlineData("10 / 0.0")]
    public void Evaluate_DivideByZero_Throws_Typed(string expression)
    {
        var ex = Should.Throw<ArithmeticExpressionException>(
            () => ArithmeticEvaluator.Evaluate(expression));

        ex.Error.ShouldBe(ExpressionError.DivideByZero);
        ex.Message.ShouldBe("Cannot divide by zero.");
    }

    [Theory]
    [InlineData("(2 + 3")]
    [InlineData("2 + 3)")]
    [InlineData("2 +")]
    [InlineData("+ ")]
    [InlineData("* 3")]
    [InlineData("2 3")]
    [InlineData("abc")]
    [InlineData("()")]
    [InlineData("2 ** 3")]
    [InlineData(".")]
    public void Evaluate_Malformed_Throws_Typed(string expression)
    {
        var ex = Should.Throw<ArithmeticExpressionException>(
            () => ArithmeticEvaluator.Evaluate(expression));

        ex.Error.ShouldBe(ExpressionError.Malformed);
    }

    [Fact]
    public void Evaluate_Null_Throws_ArgumentNull()
    {
        Should.Throw<ArgumentNullException>(() => ArithmeticEvaluator.Evaluate(null!));
    }
}
