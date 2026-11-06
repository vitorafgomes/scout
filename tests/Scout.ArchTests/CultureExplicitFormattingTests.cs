using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// An interpolation hole with a date or numeric format renders through the host's culture, so the same
/// code prints 12.50 or 12,50 depending on the machine. CA1305 covers ToString, Parse and Format; a hole
/// has no provider argument, so the sanctioned shape is string.Create(CultureInfo.InvariantCulture, $"...").
/// </summary>
public partial class CultureExplicitFormattingTests
{
    private static readonly string[] ScannedRoots = ["src", "tests"];

    /// <summary>This file states the shapes it bans, so it is the one file the scan skips.</summary>
    private const string RuleOwnFile = "CultureExplicitFormattingTests.cs";


    /// <summary>
    /// A hole whose format is a date pattern of two or more field characters, a numeric format with a
    /// precision, or one of the single letters that render through the culture (currency, exponent,
    /// fixed-point, general, percent, short time). N, D, X, O and R alone are how a Guid, a hex value or a
    /// round-trip date is formatted, and those carry no culture.
    /// </summary>
    [GeneratedRegex(@"\$@?""(?:[^""\\]|\\.)*?\{[^{}""]+?:(?:[^""}]*[dMyHhmsf][^""}]*[dMyHhmsf][^""}]*|[FNCPED]\d+|[CcEeFfGgPpTt])\}(?:[^""\\]|\\.)*?""")]
    private static partial Regex InterpolatedWithAFormat();

    /// <summary>A raw interpolated literal, <c>$"""..."""</c>, which the quoted-string pattern above cannot see.</summary>
    [GeneratedRegex(@"\$+@?""""""[\s\S]*?""""""")]
    private static partial Regex RawInterpolated();

    [GeneratedRegex(@"\{[^{}""]+?:(?:[^""}]*[dMyHhmsf][^""}]*[dMyHhmsf][^""}]*|[FNCPED]\d+|[CcEeFfGgPpTt])\}")]
    private static partial Regex HoleWithAFormat();

    internal static IEnumerable<string> UnwrappedInterpolations(string text)
    {
        var matches = InterpolatedWithAFormat().Matches(text).Cast<Match>()
            .Concat(RawInterpolated().Matches(text).Cast<Match>().Where(raw => HoleWithAFormat().IsMatch(raw.Value)))
            .OrderBy(m => m.Index);

        foreach (var match in matches)
        {
            // Only the statement the hole sits in may state the provider: look back to the previous ';'.
            var before = text[(text.LastIndexOf(';', match.Index) + 1)..match.Index];

            if (before.Contains("InvariantCulture", StringComparison.Ordinal)
             || before.Contains("FormattableString.Invariant", StringComparison.Ordinal))
            {
                continue;
            }

            yield return match.Value;
        }
    }

    private static IEnumerable<string> Offenders()
    {
        foreach (var file in SourceText.SourceFiles(ScannedRoots))
        {
            if (Path.GetFileName(file).Equals(RuleOwnFile, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var snippet in UnwrappedInterpolations(File.ReadAllText(file)))
            {
                yield return $"{SourceText.Relative(file)}  {snippet}";
            }
        }
    }

    [Fact]
    public void NoDateOrAmountIsFormattedThroughTheHostCulture()
    {
        var offenders = Offenders().OrderBy(x => x, StringComparer.Ordinal).ToList();

        offenders.ShouldBeEmpty(
            "an interpolation hole with a format renders through whatever culture the host carries, so a "
          + "quote amount or a date changes with the machine; wrap the whole string: "
          + "string.Create(CultureInfo.InvariantCulture, $\"...\")");
    }

    [Theory]
    [InlineData("""var fileName = $"quotes-{now:yyyyMMdd-HHmmss}.csv";""")]
    [InlineData("""logger.LogInformation($"Average: {avgTime:F2}ms");""")]
    [InlineData("""Reference = $"PR{id:D6}",""")]
    [InlineData("""var total = $"Total {quote.Total:C}";""")]
    [InlineData("""var share = $"{ratio:P}";""")]
    [InlineData("var body = $\"\"\"\n    {amount:N2} EUR\n    \"\"\";")]
    public void TheRuleSeesAnInterpolationHoleWithoutAProvider(string source) =>
        UnwrappedInterpolations(source).ShouldNotBeEmpty();

    [Theory]
    [InlineData("""var key = $"prefix:{id:N}";""")]
    [InlineData("""var text = $"{name} has {count} items";""")]
    [InlineData("""var stamp = $"{date:O}";""")]
    [InlineData("""var line = string.Create(CultureInfo.InvariantCulture, $"{amount:N2} EUR");""")]
    [InlineData("var name = string.Create(\n    CultureInfo.InvariantCulture,\n    $\"Export_{extractionDate:yyyyMMddHHmmss}\");")]
    [InlineData("var body = string.Create(CultureInfo.InvariantCulture, $\"\"\"\n    {amount:N2} EUR\n    \"\"\");")]
    [InlineData("var body = $\"\"\"\n    {name} wrote\n    \"\"\";")]
    public void TheRuleLeavesAloneAnInterpolationThatCarriesNoCultureOrStatesIt(string source) =>
        UnwrappedInterpolations(source).ShouldBeEmpty();

    [Fact]
    public void AProviderOnThePreviousStatementDoesNotCoverTheNextOne() =>
        UnwrappedInterpolations("var a = string.Create(CultureInfo.InvariantCulture, $\"{x:N2}\");\nvar b = $\"{y:N2}\";")
            .ShouldHaveSingleItem()
            .ShouldContain("{y:N2}");

    [Fact]
    public void TheOneSkippedFileIsStillTheReasonItIsSkipped()
    {
        var ownFile = SourceText.Files(["tests"], RuleOwnFile).Single();

        UnwrappedInterpolations(File.ReadAllText(ownFile)).ShouldNotBeEmpty(
            "the skip exists because this file spells out the banned shape; once it stops doing that the "
          + "skip is a hole rather than an exemption");
    }

    [Fact]
    public void TheRuleReadsSourceAtAll() =>
        SourceText.SourceFiles(ScannedRoots).Count().ShouldBeGreaterThan(50, "a rule that opened no file passes without asserting anything");
}
