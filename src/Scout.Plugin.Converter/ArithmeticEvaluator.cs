using System.Globalization;

namespace Scout.Plugin.Converter;

/// <summary>
/// The kind of failure produced while evaluating an arithmetic expression.
/// </summary>
internal enum ExpressionError
{
    /// <summary>The expression could not be parsed (bad token, unbalanced parentheses, trailing operator).</summary>
    Malformed,

    /// <summary>The expression asked to divide by zero.</summary>
    DivideByZero,
}

/// <summary>
/// Raised by <see cref="ArithmeticEvaluator"/> when an expression cannot be
/// evaluated. Carries an <see cref="ExpressionError"/> so the plugin can map it to
/// a short, user-safe corrective sentence. This type never escapes into the
/// kernel: the plugin catches it and returns a string.
/// </summary>
internal sealed class ArithmeticExpressionException : Exception
{
    public ArithmeticExpressionException(ExpressionError error, string message)
        : base(message) => Error = error;

    public ExpressionError Error { get; }
}

/// <summary>
/// A small, self-contained recursive-descent evaluator for decimal arithmetic:
/// addition, subtraction, multiplication, division, parentheses, and unary
/// plus/minus. It is deliberately hand-written; the plugin must never delegate
/// arithmetic to the LLM or to <c>eval</c>, Roslyn, or <c>DataTable.Compute</c>.
/// Grammar (lowest to highest precedence):
/// <code>
/// expression := term (('+' | '-') term)*
/// term       := factor (('*' | '/') factor)*
/// factor     := ('+' | '-') factor | primary
/// primary    := number | '(' expression ')'
/// </code>
/// </summary>
internal static class ArithmeticEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="expression"/> and return its numeric value.
    /// </summary>
    /// <exception cref="ArithmeticExpressionException">
    /// The expression is malformed or divides by zero.
    /// </exception>
    public static double Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var parser = new Parser(expression);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                var c = Peek();
                if (c == '+')
                {
                    _pos++;
                    value += ParseTerm();
                }
                else if (c == '-')
                {
                    _pos++;
                    value -= ParseTerm();
                }
                else
                {
                    break;
                }
            }

            return value;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos != text.Length)
            {
                throw Malformed();
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                var c = Peek();
                if (c == '*')
                {
                    _pos++;
                    value *= ParseFactor();
                }
                else if (c == '/')
                {
                    _pos++;
                    var divisor = ParseFactor();
                    if (divisor == 0)
                    {
                        throw new ArithmeticExpressionException(
                            ExpressionError.DivideByZero, "Cannot divide by zero.");
                    }

                    value /= divisor;
                }
                else
                {
                    break;
                }
            }

            return value;
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            var c = Peek();
            if (c == '+')
            {
                _pos++;
                return ParseFactor();
            }

            if (c == '-')
            {
                _pos++;
                return -ParseFactor();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            var c = Peek();
            if (c == '(')
            {
                _pos++;
                var value = ParseExpression();
                SkipWhitespace();
                if (Peek() != ')')
                {
                    throw Malformed();
                }

                _pos++;
                return value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var start = _pos;
            var seenDot = false;
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsAsciiDigit(c))
                {
                    _pos++;
                }
                else if (c == '.' && !seenDot)
                {
                    seenDot = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            var slice = text[start.._pos];
            if (slice.Length == 0 || slice == ".")
            {
                throw Malformed();
            }

            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw Malformed();
            }

            return value;
        }

        private void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private static ArithmeticExpressionException Malformed()
            => new(ExpressionError.Malformed, "The expression could not be parsed.");
    }
}
