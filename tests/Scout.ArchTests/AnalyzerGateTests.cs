using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Protects the analyzer gate from being switched off piecemeal. The gated rules are raised to warning once, in the
/// root .editorconfig; a nested .editorconfig, a project file or a #pragma that lowers one leaves the diagnostics
/// script unable to see that rule in that project, and no other check would notice.
/// </summary>
public partial class AnalyzerGateTests
{
    /// <summary>One key and value read from an .editorconfig, with the section header it sits under.</summary>
    internal readonly record struct Setting(string Section, string Key, string Value)
    {
        /// <summary>True before the first header and under a header whose glob names no folder, such as [*] or [*.cs].</summary>
        public bool AppliesEverywhere => Section.Contains('/') is false;
    }

    /// <summary>The rules the root .editorconfig raises from suggestion or info; a rule joins this set in the change that raises it.</summary>
    private static readonly HashSet<string> Gated = new(StringComparer.OrdinalIgnoreCase)
    {
        "IDE0005",
        "IDE0042",
        "IDE0051",
        "IDE0052",
        "IDE0059",
        "IDE0060",
        "IDE0130",
        "IDE0270",
        "IDE0290",
        "CA1305",
        "CA1826",
        "CA1859",
        "CA1865",
        "CA2016",
        "SYSLIB1045",
    };

    /// <summary>
    /// The one project file allowed to name a gated rule: its C# is produced from worker.proto by Grpc.Tools and
    /// IDE0005 fires in that output. Naming the project and the rule here stops a second rule joining it unseen.
    /// </summary>
    private static readonly (string Project, string Rule) ProtoOutputException =
        ("src/Scout.ControlPlane.Contracts/Scout.ControlPlane.Contracts.csproj", "IDE0005");

    private static readonly string[] PropertiesOnlyTheRootSets = ["AnalysisMode", "AnalysisLevel", "EnforceCodeStyleInBuild"];

    [GeneratedRegex(@"\b[A-Za-z]+\d{4}\b")]
    private static partial Regex DiagnosticId();

    [GeneratedRegex(@"^dotnet_diagnostic\.(?<rule>[^.]+)\.severity$", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityKey();

    [GeneratedRegex(@"#pragma\s+warning\s+disable\b")]
    private static partial Regex PragmaDisable();

    private static string InRepo(string relative) => Path.Combine(ArchitectureTests.SolutionRoot(), relative);

    private static bool KeepsTheGate(string severity) => severity is "warning" or "error";

    /// <summary>The key and value lines of an .editorconfig in file order; headers set the section, comment lines are passed over.</summary>
    internal static IEnumerable<Setting> Settings(string editorConfig)
    {
        var section = string.Empty;

        foreach (var line in editorConfig.Split('\n').Select(raw => raw.Trim()))
        {
            if (line is ['[', .., ']'])
            {
                section = line;
            }
            else if (line is not ['#' or ';', ..] && line.IndexOf('=') is var equals and > 0)
            {
                yield return new Setting(section, line[..equals].TrimEnd(), line[(equals + 1)..].TrimStart());
            }
        }
    }

    /// <summary>The severity each rule ends up with; a later setting for the same rule replaces an earlier one.</summary>
    internal static Dictionary<string, string> RuleSeverities(IEnumerable<Setting> settings)
    {
        var severities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in settings)
        {
            if (SeverityKey().Match(setting.Key) is { Success: true } key)
            {
                severities[key.Groups["rule"].Value] = setting.Value;
            }
        }

        return severities;
    }

    private static IEnumerable<string> GatedRulesNamedIn(string text) =>
        DiagnosticId().Matches(text).Select(id => id.Value).Where(Gated.Contains).Distinct(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheRootConfigRaisesEveryGatedRule()
    {
        var everywhere = Settings(File.ReadAllText(InRepo(".editorconfig"))).Where(setting => setting.AppliesEverywhere);
        var severities = RuleSeverities(everywhere);

        var notRaised = Gated
            .Where(rule => severities.TryGetValue(rule, out var severity) is false || KeepsTheGate(severity) is false)
            .Order(StringComparer.Ordinal)
            .ToList();

        notRaised.ShouldBeEmpty(
            "a rule left at suggestion or info is never reported by the command-line build, so the diagnostics script "
          + "passes without having looked; set it to warning in the root .editorconfig under [*.cs], not under a "
          + "section that names a folder");
    }

    [Fact]
    public void NoNestedConfigOptsAProjectOut()
    {
        var optOuts = new List<string>();

        foreach (var file in SourceText.FilesNamed(".editorconfig", "src", "tests"))
        {
            var settings = Settings(File.ReadAllText(file)).ToList();
            var where = SourceText.RepoPath(file);

            optOuts.AddRange(RuleSeverities(settings)
                .Where(pair => Gated.Contains(pair.Key) && KeepsTheGate(pair.Value) is false)
                .Select(pair => $"{where} sets {pair.Key} to {pair.Value}"));

            optOuts.AddRange(settings
                .Where(setting => setting.Key.Equals("generated_code", StringComparison.OrdinalIgnoreCase)
                               && setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                .Select(_ => $"{where} declares its files generated"));
        }

        optOuts.ShouldBeEmpty(
            "a nested .editorconfig that lowers a gated rule or declares the code generated switches the gate off for "
          + "one project, and a clean diagnostics run then says nothing about it; Scout has no generated tree");
    }

    [Fact]
    public void NoProjectFileRelaxesTheBuild()
    {
        var relaxed = new List<string>();

        foreach (var projectFile in SourceText.FilesNamed("*.csproj", "src", "tests"))
        {
            var text = File.ReadAllText(projectFile);
            var where = SourceText.RepoPath(projectFile);
            var elements = XDocument.Parse(text).Descendants().ToList();

            relaxed.AddRange(GatedRulesNamedIn(text)
                .Where(rule => (where, rule.ToUpperInvariant()) != ProtoOutputException)
                .Select(rule => $"{where} names {rule}, in a NoWarn or a WarningsNotAsErrors"));

            relaxed.AddRange(elements
                .Where(element => element.Name.LocalName == "TreatWarningsAsErrors"
                               && element.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                .Select(_ => $"{where} turns TreatWarningsAsErrors off"));

            relaxed.AddRange(elements
                .Where(element => PropertiesOnlyTheRootSets.Contains(element.Name.LocalName))
                .Select(element => $"{where} sets {element.Name.LocalName}"));
        }

        relaxed.ShouldBeEmpty("how strict the build is gets decided in Directory.Build.props and the root .editorconfig, never per project");
    }

    [Fact]
    public void NoPragmaSilencesAGatedRuleWithoutAReason()
    {
        var silent = new List<string>();

        foreach (var file in SourceText.CSharpUnder("src", "tests"))
        {
            var text = File.ReadAllText(file);
            var code = SourceText.CodeOnly(text).Split('\n');
            var comments = SourceText.CommentText(text).Split('\n');

            silent.AddRange(Enumerable.Range(0, code.Length)
                .Where(row => PragmaDisable().IsMatch(code[row]) && GatedRulesNamedIn(code[row]).Any())
                .Where(row => string.IsNullOrWhiteSpace(comments[row]))
                .Select(row => $"{SourceText.RepoPath(file)}:{row + 1}"));
        }

        silent.ShouldBeEmpty("a #pragma that disables a gated rule says why in a comment on the same line; otherwise fix the code");
    }

    [Fact]
    public void TheBuildPropsKeepTheStrictSettings()
    {
        var values = XDocument.Load(InRepo("Directory.Build.props"))
            .Descendants()
            .Where(element => element.HasElements is false)
            .Select(element => $"{element.Name.LocalName}={element.Value.Trim()}")
            .ToList();

        values.ShouldContain("TreatWarningsAsErrors=true");
        values.ShouldContain("AnalysisMode=Recommended");
        values.ShouldContain("AnalysisLevel=latest");
    }

    [Fact]
    public void TheParserReadsSectionsAndTheRootConfig()
    {
        RuleSeverities(Settings(File.ReadAllText(InRepo(".editorconfig"))).Where(setting => setting.AppliesEverywhere))
            .Count.ShouldBeGreaterThanOrEqualTo(Gated.Count);

        var sample = Settings("[*]\ndotnet_diagnostic.CA1826.severity = error\n; note\n[src/**.cs]\ndotnet_diagnostic.IDE0059.severity=silent\n").ToList();

        RuleSeverities(sample).ShouldBe(new Dictionary<string, string> { ["CA1826"] = "error", ["IDE0059"] = "silent" });
        RuleSeverities(sample.Where(setting => setting.AppliesEverywhere)).ShouldBe(new Dictionary<string, string> { ["CA1826"] = "error" });
    }
}
