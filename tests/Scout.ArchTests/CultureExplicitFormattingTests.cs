using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// A formatted hole in an interpolated string, such as a date pattern or an amount with a precision, prints through
/// the culture of the machine Scout runs on: 3.50 on one, 3,50 on the next. CA1305 catches the calls that take a
/// format provider, and a hole takes none, so the whole string is built with string.Create and the invariant culture.
/// </summary>
public partial class CultureExplicitFormattingTests
{
    /// <summary>The file that writes the forbidden shapes down as test data, and so is the one file its own scan leaves out.</summary>
    private const string ThisFile = "CultureExplicitFormattingTests.cs";

    private const string CultureLetters = "CcEeFfGgPpTt";
    private const string LettersTakingAPrecision = "FNCPED";
    private const string DateFieldLetters = "dMyHhmsf";

    [GeneratedRegex(@"\{(?<hole>[^{}""]+)\}")]
    private static partial Regex Hole();

    /// <summary>
    /// True for a currency, exponent, fixed-point, general, percent or short-time letter on its own, for one of the
    /// numeric letters followed by a precision, and for a pattern holding two or more date or time field letters.
    /// N, D, X, O and R on their own carry no culture: they print a Guid, a hex value or a round-trip date.
    /// </summary>
    internal static bool PrintsThroughTheCulture(string format) => format switch
    {
        [var letter] => CultureLetters.Contains(letter),
        [var letter, .. var precision] when LettersTakingAPrecision.Contains(letter) && precision.All(char.IsDigit) => true,
        _ => format.Count(DateFieldLetters.Contains) >= 2,
    };

    /// <summary>Every interpolated literal in the text that has a culture-bound hole and whose statement names no invariant provider before it.</summary>
    internal static IEnumerable<string> CultureBoundInterpolations(string text)
    {
        var searchFrom = 0;

        while (text.IndexOf('$', searchFrom) is var dollar and >= 0)
        {
            var end = InterpolatedLiteralEnd(text, dollar);

            if (end < 0)
            {
                searchFrom = dollar + 1;
                continue;
            }

            var literal = text[dollar..end];
            searchFrom = end;

            if (Hole().Matches(literal).Any(hole => HasCultureBoundFormat(hole.Groups["hole"].Value))
             && StatementNamesAProvider(text, dollar) is false)
            {
                yield return literal;
            }
        }
    }

    private static bool HasCultureBoundFormat(string hole) =>
        Enumerable.Range(1, Math.Max(0, hole.Length - 1))
            .Where(colon => hole[colon] == ':')
            .Any(colon => PrintsThroughTheCulture(hole[(colon + 1)..]));

    private static bool StatementNamesAProvider(string text, int literalStart)
    {
        var statementStart = text.LastIndexOf(';', literalStart) + 1;
        var lead = text.AsSpan(statementStart, literalStart - statementStart);

        return lead.Contains("InvariantCulture", StringComparison.Ordinal)
            || lead.Contains("FormattableString.Invariant", StringComparison.Ordinal);
    }

    private static int InterpolatedLiteralEnd(string text, int dollar)
    {
        var at = dollar;

        while (at < text.Length && text[at] == '$')
        {
            at++;
        }

        var verbatim = (dollar > 0 && text[dollar - 1] == '@') || (at < text.Length && text[at] == '@');

        if (at < text.Length && text[at] == '@')
        {
            at++;
        }

        var quotes = 0;

        while (at + quotes < text.Length && text[at + quotes] == '"')
        {
            quotes++;
        }

        return quotes switch
        {
            1 => ClosingQuoteEnd(text, at + 1, verbatim),
            >= 3 => ClosingRunEnd(text, at + quotes, quotes),
            _ => -1,
        };
    }

    private static int ClosingQuoteEnd(string text, int bodyStart, bool verbatim)
    {
        for (var k = bodyStart; k < text.Length; k++)
        {
            if (text[k] == '\\' && verbatim is false)
            {
                k++;
            }
            else if (text[k] == '"' && verbatim && k + 1 < text.Length && text[k + 1] == '"')
            {
                k++;
            }
            else if (text[k] == '"')
            {
                return k + 1;
            }
        }

        return -1;
    }

    private static int ClosingRunEnd(string text, int bodyStart, int length)
    {
        var delimiter = new string('"', length);
        var close = text.IndexOf(delimiter, bodyStart, StringComparison.Ordinal);

        return close < 0 ? -1 : close + length;
    }

    [Fact]
    public void NoDateOrAmountIsFormattedThroughTheHostCulture()
    {
        var unwrapped =
            from file in SourceText.CSharpUnder("src", "tests")
            where Path.GetFileName(file) != ThisFile
            from literal in CultureBoundInterpolations(File.ReadAllText(file))
            select SourceText.RepoPath(file) + ": " + literal;

        unwrapped.ToList().ShouldBeEmpty(
            "a formatted hole prints a date or an amount in the culture of whatever machine runs Scout; build the "
          + "string as string.Create(CultureInfo.InvariantCulture, $\"...\") so every machine prints the same text");
    }

    [Theory]
    [InlineData("""var name = $"report-{at:yyyy-MM-dd}.txt";""")]
    [InlineData("""logger.LogDebug($"took {elapsed:F1} s");""")]
    [InlineData("""Code = $"INV{number:D5}",""")]
    [InlineData("""var price = $"Price {amount:c}";""")]
    [InlineData("""var growth = $"{rate:E}";""")]
    [InlineData("var text = $\"\"\"\n    {total:N0} items\n    \"\"\";")]
    [InlineData("var json = $$\"\"\"\n    { \"at\": \"{{when:HH:mm}}\" }\n    \"\"\";")]
    public void AFormattedHoleWithoutAProviderIsReported(string source) =>
        CultureBoundInterpolations(source).ShouldNotBeEmpty();

    [Theory]
    [InlineData("""var id = $"run-{guid:N}";""")]
    [InlineData("""var note = $"{user} sent {files} files";""")]
    [InlineData("""var iso = $"{moment:O}";""")]
    [InlineData("""var hex = $"0x{value:X4}";""")]
    [InlineData("""var cost = string.Create(CultureInfo.InvariantCulture, $"{cost:F2} GBP");""")]
    [InlineData("""var share = FormattableString.Invariant($"{ratio:P1}");""")]
    [InlineData("var file = string.Create(\n    CultureInfo.InvariantCulture,\n    $\"backup_{stamp:yyyyMMdd}\");")]
    [InlineData("var text = string.Create(CultureInfo.InvariantCulture, $\"\"\"\n    {cost:N1} GBP\n    \"\"\");")]
    [InlineData("var text = $\"\"\"\n    {author} replied\n    \"\"\";")]
    public void AnInterpolationWithoutACultureOrWithAProviderPasses(string source) =>
        CultureBoundInterpolations(source).ShouldBeEmpty();

    [Fact]
    public void AProviderCoversOnlyTheStatementItIsWrittenIn() =>
        CultureBoundInterpolations("var first = string.Create(CultureInfo.InvariantCulture, $\"{a:F3}\");\nvar second = $\"{b:F3}\";")
            .ShouldHaveSingleItem()
            .ShouldContain("{b:F3}");

    [Fact]
    public void TheFileLeftOutOfTheScanStillHoldsTheForbiddenShape()
    {
        var thisFile = SourceText.FilesNamed(ThisFile, "tests").Single();

        CultureBoundInterpolations(File.ReadAllText(thisFile)).ShouldNotBeEmpty(
            "this file is left out only because its test data writes the forbidden shape; without that data the "
          + "exclusion would hide a real offender");
    }

    [Fact]
    public void TheScanOpensTheSourceTree() =>
        SourceText.CSharpUnder("src", "tests").Count().ShouldBeGreaterThan(50, "a scan that opened almost nothing passes without proving anything");
}
