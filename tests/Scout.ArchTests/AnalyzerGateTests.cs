using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// The analyzer gate stays on. A nested .editorconfig, a NoWarn in a project or a bare #pragma that lowers one
/// of the gated rules makes scripts/check-diagnostics.sh blind to that rule for that project, silently.
/// </summary>
public class AnalyzerGateTests
{
    /// <summary>The rules the root .editorconfig raises to warning; a rule joins this list in the same change that raises it.</summary>
    private static readonly string[] GatedRules =
    [
        "IDE0005", "IDE0042", "IDE0051", "IDE0052", "IDE0059", "IDE0060", "IDE0130", "IDE0270", "IDE0290",
        "CA1305", "CA1826", "CA1859", "CA1865", "CA2016", "SYSLIB1045"
    ];

    private static readonly string[] AcceptedSeverities = ["warning", "error"];

    private static string Root => ArchitectureTests.SolutionRoot();

    private static string RootEditorConfig => Path.Combine(Root, ".editorconfig");

    /// <summary>
    /// The `dotnet_diagnostic.CODE.severity = value` pairs in an .editorconfig text. With globalOnly, an entry
    /// counts only under a section whose glob has no path (`[*]`, `[*.cs]`): a severity under `[tests/**.cs]`
    /// gates nothing in src/.
    /// </summary>
    internal static Dictionary<string, string> Severities(string editorConfig, bool globalOnly = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sectionIsGlobal = true;

        foreach (var raw in editorConfig.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sectionIsGlobal = line.Contains('/') is false;
                continue;
            }

            if (line.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase) is false
             || (globalOnly && sectionIsGlobal is false))
            {
                continue;
            }

            var parts = line.Split('=', 2);
            var key = parts[0].Trim().Split('.');

            if (parts.Length == 2 && key.Length == 3 && key[2].Equals("severity", StringComparison.OrdinalIgnoreCase))
            {
                result[key[1]] = parts[1].Trim();
            }
        }

        return result;
    }

    private static List<string> NestedEditorConfigs() =>
        SourceText.Files(["src", "tests"], ".editorconfig").OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void EveryGatedRuleIsConfiguredAsAWarningAtTheRoot()
    {
        var severities = Severities(File.ReadAllText(RootEditorConfig), globalOnly: true);

        var lowered = GatedRules
            .Where(rule => severities.TryGetValue(rule, out var severity) is false || AcceptedSeverities.Contains(severity) is false)
            .ToList();

        lowered.ShouldBeEmpty(
            "a gated rule left at its shipped severity (suggestion or info) never reaches the CLI, so the "
          + "diagnostics gate passes without seeing it; raise it to warning in the root .editorconfig, under "
          + "[*.cs], not under a path-scoped section such as [tests/**.cs]");
    }

    [Fact]
    public void NoNestedEditorConfigLowersAGatedRule()
    {
        var offenders = new List<string>();

        foreach (var file in NestedEditorConfigs())
        {
            var text = File.ReadAllText(file);
            var severities = Severities(text);

            offenders.AddRange(GatedRules
                .Where(rule => severities.TryGetValue(rule, out var severity) && AcceptedSeverities.Contains(severity) is false)
                .Select(rule => $"{SourceText.Relative(file)} lowers {rule}"));

            var isGenerated = text.Split('\n').Any(line => line.Replace(" ", "", StringComparison.Ordinal).Equals("generated_code=true", StringComparison.OrdinalIgnoreCase));

            if (isGenerated)
            {
                offenders.Add($"{SourceText.Relative(file)} marks hand-written code as generated");
            }
        }

        offenders.ShouldBeEmpty(
            "an opt-out in a nested .editorconfig turns the gate off for that project alone, and a clean run "
          + "then proves nothing there; Scout has no generated tree that may opt out");
    }

    /// <summary>
    /// The one project whose NoWarn may name a gated rule: its C# is generated from worker.proto by Grpc.Tools, and
    /// IDE0005 fires in that output. Its NoWarn is listed here so a second rule cannot join it unnoticed.
    /// </summary>
    private const string GeneratedCodeProject = "src/Scout.ControlPlane.Contracts/Scout.ControlPlane.Contracts.csproj";

    private static readonly string[] GeneratedCodeProjectMayLower = ["IDE0005"];

    [Fact]
    public void NoProjectFileLoosensTheBuild()
    {
        var offenders = new List<string>();

        foreach (var csproj in SourceText.Files(["src", "tests"], "*.csproj"))
        {
            var text = File.ReadAllText(csproj);
            var relative = SourceText.Relative(csproj);
            var allowed = relative == GeneratedCodeProject ? GeneratedCodeProjectMayLower : [];

            if (GatedRules.Except(allowed).Any(rule => text.Contains(rule, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add($"{relative} names a gated rule (a NoWarn or a WarningsNotAsErrors)");
            }

            foreach (var property in new[] { "<TreatWarningsAsErrors>false", "<AnalysisMode>", "<AnalysisLevel>", "<EnforceCodeStyleInBuild>" })
            {
                if (text.Contains(property, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relative} sets {property}");
                }
            }
        }

        offenders.ShouldBeEmpty("the build strictness lives in Directory.Build.props and the root .editorconfig only");
    }

    [Fact]
    public void NoPragmaDisablesAGatedRuleWithoutAReason()
    {
        var offenders = new List<string>();

        foreach (var file in SourceText.SourceFiles("src", "tests"))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.Contains("#pragma warning disable", StringComparison.Ordinal)
                 && GatedRules.Any(rule => line.Contains(rule, StringComparison.OrdinalIgnoreCase))
                 && line.Contains("//", StringComparison.Ordinal) is false)
                {
                    offenders.Add($"{SourceText.Relative(file)}:{i + 1}");
                }
            }
        }

        offenders.ShouldBeEmpty("a #pragma that silences a gated rule carries its reason on the same line, or the rule is fixed");
    }

    [Fact]
    public void TheBuildStaysStrict()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        props.ShouldContain("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
        props.ShouldContain("<AnalysisMode>Recommended</AnalysisMode>");
        props.ShouldContain("<AnalysisLevel>latest</AnalysisLevel>");
    }

    [Fact]
    public void TheRuleReadsTheConfigAtAll()
    {
        Severities(File.ReadAllText(RootEditorConfig), globalOnly: true).Count.ShouldBeGreaterThanOrEqualTo(GatedRules.Length);

        const string sample = "[*.cs]\ndotnet_diagnostic.CA1305.severity = warning\n# comment\n[tests/**.cs]\ndotnet_diagnostic.IDE0005.severity=none\n";
        Severities(sample).ShouldBe(new Dictionary<string, string> { ["CA1305"] = "warning", ["IDE0005"] = "none" });
        Severities(sample, globalOnly: true).ShouldBe(new Dictionary<string, string> { ["CA1305"] = "warning" });
    }
}
