using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>
/// The names a finding's quote calls, found by a small lexical rule and no parser: an identifier directly followed by an
/// opening parenthesis, the last segment of a qualified call included (<c>a.b(</c>, <c>a::b(</c>, <c>a-&gt;b(</c>), in the
/// order they appear, each once, never a control or declaration keyword. A name inside a string literal or a comment is
/// taken too: the rule does not know them. The quote is data from the pull request; nothing here interprets it.
/// </summary>
public static partial class CalledNames
{
    /// <summary>The names given for one quote at most.</summary>
    public const int MaxNames = 8;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "sizeof", "typeof", "nameof",
        "function", "def", "fn", "func", "when", "default", "checked", "unchecked", "fixed",
    };

    [GeneratedRegex(@"(?<![A-Za-z0-9_$])([A-Za-z_$][A-Za-z0-9_$]*)\(")]
    private static partial Regex Call();

    /// <summary>The names <paramref name="quote"/> calls, at most <see cref="MaxNames"/>.</summary>
    public static IReadOnlyList<string> In(string? quote)
    {
        var names = new List<string>();
        foreach (Match call in Call().Matches(quote ?? string.Empty))
        {
            var name = call.Groups[1].Value;
            if (!Keywords.Contains(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
                if (names.Count == MaxNames)
                {
                    break;
                }
            }
        }

        return names;
    }
}
