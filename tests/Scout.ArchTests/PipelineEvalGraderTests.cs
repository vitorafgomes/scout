using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// The scout-pipeline eval graders accept the answer their case asks for and reject one that denies it. Each grader
/// is a regex in the frontmatter of a Markdown file; this class reads it the way the eval runner applies it
/// (contains, with the file's flags) and feeds it fixed answers.
/// </summary>
public class PipelineEvalGraderTests
{
    private const string BugfixGraders = ".claude/skills/scout-pipeline/evals/classifies-bugfix/graders/";

    private const string Denial = "This is not a Bug; the full pipeline does not apply.";

    private const string ContractsGraders = ".claude/skills/scout-pipeline/evals/contracts-test-surface/graders/";

    private const string ContractsSurface = "Run every test project under tests/, and in ui/ run bun run typecheck, bun run lint, bun run format and bun run test.";

    private const string DocOnlyGraders = ".claude/skills/scout-pipeline/evals/fast-path-doc-only/graders/";

    private const string OutboundGraders = ".claude/skills/scout-pipeline/evals/never-fast-path-outbound-call/graders/";

    private const string OutboundAnswer = "Classification: Feature. Shape: full pipeline, never fast path: it adds a call leaving the process, an executive write on GitHub (Rule A). Lenses gate 6 runs: invariants, read-only, boundaries. First page: context/pages/services/plugin-github.md.";

    private const string OutboundAnswerSayingNotReadOnly = "This call is not read-only: adding a label writes to GitHub, so it is an executive write and the `read-only` lens runs. Not a fast path; full pipeline.";

    private const string ContractsRealAnswerOne = """
    Gate 4 for this change uses the `src/Scout.ControlPlane.Contracts` row of the pipeline's surface table. Contracts is named in that row outright, and it's also referenced by three or more projects (hosts, control plane, desktop UI), so the same row applies either way.

    **1. Every .NET test project under `tests/`, except `tests/old`**
    - Run each one once with no `--filter`. You can use `--filter` earlier for a subset, but the full unfiltered run has to happen before the commit message.
    - Each project must report `N passed` with Skipped = 0.
    - This set already covers what the narrower rows would add:
      - the test project for each host, plus every adapter and plugin test project (the `src/Scout.Host.*` row)
      - the control-plane test project
      - `tests/Scout.ArchTests` (layering rules, analyzer gate, test-suite health, source scanners). This also runs on its own because Rule B means the change will touch `tests/`.

    **2. The four checks in `ui/`, because the desktop UI consumes this contract**
    - `bun run typecheck`
    - `bun run lint`
    - `bun run format`
    - `bun run test` (pass counts per package)

    The change counts as done only when both groups are green.

    I couldn't list the actual test project names: neither `tests/*/*.csproj` nor `ui/package.json` exists under the current working directory. To get the exact list, check `context/pages/architecture/project-map.md` or the `tests/` folder in the real worktree.

    As you asked, I edited nothing and stopped at gate 4.
    """;

    private const string ContractsRealAnswerTwo = """
    Gate 4 needs two surfaces green. The row that applies is "`src/Scout.ControlPlane.Contracts` or anything three or more projects reference".

    **1. Every .NET test project under `tests/`, except `tests/old`**
    - This includes `tests/Scout.ArchTests`, along with the host, control-plane, adapter and plugin test projects.
    - You may use `--filter` for a named subset while proving something. Before the commit message, the full surface must also run once with no filter.
    - Each project must report `N passed` and a Skipped count of 0.

    **2. The four `ui/` workspace checks, because the desktop UI consumes this contract**

    Run these in `ui/`:
    - `bun run typecheck`
    - `bun run lint`
    - `bun run format`
    - `bun run test` (counts reported per package)

    Rows that add nothing here:
    - **Host/DI row** (the host's test project plus every adapter and plugin test project): those projects are already in surface 1. It would only matter as a separate trigger if the change also touched a `src/Scout.Host.*` project or DI registration.
    - **Build-config row** (`.editorconfig`, `Directory.*.props`, `Scout.slnx`, anything under `tests/`): this would add `tests/Scout.ArchTests`, which is already in surface 1.

    I couldn't list the actual test project names. There's no `tests/*/*.csproj` under the working directory, so the full list should be taken from `tests/` in the Scout worktree.

    I edited nothing.
    """;

    private const string OutboundRealAnswerOne = """
    I didn't edit anything. I also didn't run the step-0 tree check (`git status --porcelain`, `git diff HEAD --stat`), and I couldn't find the `context/` pages from the working directory. The answer below comes from the pipeline skill's rules alone.

    **Classification: Feature.** It adds a new plugin operation: labelling the issue after summarising it.

    **Shape: full pipeline, not fast path.** Being one line doesn't qualify it. The fast path's single-line `.cs` rule only covers a line with no logic, signature, query or DI impact. This line also falls under the "never trivial" list twice: it adds a call that leaves the process (a GitHub API write through the client) and it adds a plugin operation.

    The bigger problem is the write itself. Labelling changes a GitHub issue, which is outside Scout's own config. Under ADR 0009 and the no-executive-writes rule, that makes it an executive write, which is forbidden. Unclear cases are forbidden by default. So the plan would most likely be: don't add the call, and stop before any edit. The only way through is a superseding ADR, and that's the owner's decision.

    **Gate 6 review lenses:**
    - `invariants`: always runs.
    - `read-only`: the change adds a call leaving the process and a plugin operation.
    - `boundaries`: the change is in a plugin and uses the vendor GitHub client, which crosses the plugin abstraction.
    - `secrets` isn't needed because no config binding, token or credential handling changes; the existing client is reused. `performance` isn't required either: the change doesn't touch streaming, indexing, a hosted service, the control plane or an LLM provider call path.

    **First page I'd read:** `context/pages/services/scout-plugin-github.md`, the project page that gate 1 requires before anything else. Because this adds a call that leaves the process, the capability check means I'd go straight on to ADR 0009 and `context/pages/invariants/no-executive-writes.md`. Those two decide whether this change is allowed at all.
    """;

    private const string OutboundRealAnswerTwo = """
    **Classification: Feature.** The plugin gains something it can't do today: adding a label to a GitHub issue.

    **Shape: full pipeline, not the fast path.** The change is one line, but the skill's never-trivial list includes "anything that adds or changes a call leaving the process (an SDK call…)" and "a plugin operation". A label call on the GitHub client is both, so diff size doesn't matter.

    There's a bigger problem. Adding a label changes state in GitHub, which is outside Scout's own config. Under ADR 0009 and the `no-executive-writes` invariant, that is an executive write, and executive writes are forbidden ("boundary cases default to forbidden"). A plan can't include a step that breaks an invariant. So the pipeline would most likely stop at gate 1 and return to the owner: either the work is dropped, or a superseding ADR comes first. The implementer would not be dispatched.

    **Gate 6 review lenses:**
    - `invariants`: always runs.
    - `read-only`: the change adds a call leaving the process and a plugin operation.
    - `boundaries`: the change touches a plugin and the GitHub client behind the plugin abstraction.
    - `secrets` is not required unless the change touches token or config binding.
    - `performance` is not mandatory, because none of its trigger areas is touched.

    **First page I would read:** the project page, `context/pages/services/scout-plugin-github.md`. I'm assuming that filename from the `context/pages/services/<project>.md` convention. The `context/` folder isn't under the current working directory, so I couldn't check it exists. After that page I'd read ADR 0009 and `context/pages/invariants/no-executive-writes.md`, as gate 1 requires for a call leaving the process. Before any of that, step 0 says to run `git status --porcelain` and `git diff HEAD --stat`.

    **Skills in play:** `scout-pipeline` (used). `ponytail` would run after the context folder reading if the change went ahead. None of the others apply at this stage.

    I've stopped here and haven't edited any files.
    """;

    private static readonly Dictionary<string, string> TableOfCase = new(StringComparer.Ordinal)
    {
        ["classifies-bugfix"] = nameof(TheBugfixGradersAcceptTheClassificationAndRejectItsDenial),
        ["contracts-test-surface"] = nameof(TheContractsSurfaceGradersAcceptTheWideSurfaceAndRejectItsDenial),
        ["fast-path-doc-only"] = nameof(TheDocOnlyGraderAcceptsTheFastPathAndRejectsItsDenial),
        ["never-fast-path-outbound-call"] = nameof(TheOutboundCallGradersAcceptTheSafeguardsAndRejectTheirDenial),
    };

    /// <summary>The graders of every case under the pipeline's <c>evals/</c>, as case directory and grader file name; a directory with no <c>graders/</c>, such as the eval runner's <c>results/</c>, is not a case.</summary>
    internal static IReadOnlyList<(string Case, string Grader)> EvalGraders() =>
        Directory.EnumerateDirectories(Path.Combine(ArchitectureTests.SolutionRoot(), ".claude", "skills", "scout-pipeline", "evals"))
            .Where(dir => Directory.Exists(Path.Combine(dir, "graders")))
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(dir, "graders"), "*.md")
                .Select(file => (Case: Path.GetFileName(dir), Grader: Path.GetFileName(file))))
            .OrderBy(g => g.Case, StringComparer.Ordinal).ThenBy(g => g.Grader, StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether the table of <paramref name="evalCase"/> holds, for <paramref name="grader"/>, a row it must accept and a row it must reject.</summary>
    internal static bool HasAnAcceptedAndARejectedRow(string evalCase, string grader)
    {
        if (!TableOfCase.TryGetValue(evalCase, out var method))
        {
            return false;
        }

        var theory = typeof(PipelineEvalGraderTests).GetMethod(method)!;
        var rows = theory.GetCustomAttributes<InlineDataAttribute>()
            .SelectMany(row => row.GetData(theory))
            .Where(data => grader.Equals(data[0] as string, StringComparison.Ordinal))
            .Select(data => (bool)data[2])
            .ToList();

        return rows.Contains(true) && rows.Contains(false);
    }

    [Fact]
    public void EveryGraderOfEveryEvalCaseHasAnAcceptedAndARejectedRowInTheTable()
    {
        var graders = EvalGraders();

        graders.ShouldNotBeEmpty("no grader found under .claude/skills/scout-pipeline/evals");
        graders.Where(g => !HasAnAcceptedAndARejectedRow(g.Case, g.Grader)).Select(g => $"{g.Case}/graders/{g.Grader}").ShouldBeEmpty(
            "a grader under .claude/skills/scout-pipeline/evals has no row it must accept and no row it must reject in this table, so a pattern that accepts a denial or rejects the right answer lands untested");
    }

    internal static Regex Grader(string relative)
    {
        var frontmatter = File.ReadAllText(Path.Combine(ArchitectureTests.SolutionRoot(), relative)).Split('\n');
        string Field(string name) =>
            frontmatter.Single(line => line.StartsWith(name + ":", StringComparison.Ordinal))[(name.Length + 1)..].Trim();

        Field("type").ShouldBe("regex");
        Field("match").ShouldBe("contains");

        var pattern = JsonSerializer.Deserialize<string>(Field("pattern")) ?? string.Empty;
        var options = JsonSerializer.Deserialize<string>(Field("flags"))?.Contains('i', StringComparison.Ordinal) is true
            ? RegexOptions.IgnoreCase
            : RegexOptions.None;

        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("lane.md", Denial, false)]
    [InlineData("not-fast-path.md", Denial, false)]
    [InlineData("lane.md", "It isn't a bug, it is a Feature: full pipeline.", false)]
    [InlineData("not-fast-path.md", "Classification: Bug. The full pipeline is not needed; this is the fast path.", false)]
    [InlineData("lane.md", "Classification: **Bug** - a provider drops the partial answer on HTTP 429. Shape: full pipeline, never fast path: it changes an LLM provider call path. Lane file: bugfix.md. Implemented by scout-implementer.", true)]
    [InlineData("not-fast-path.md", "Classification: **Bug** - a provider drops the partial answer on HTTP 429. Shape: full pipeline, never fast path: it changes an LLM provider call path. Lane file: bugfix.md. Implemented by scout-implementer.", true)]
    [InlineData("lane.md", "Bug fix lane (bugfix.md). Not a fast path: the change touches the provider's streaming path, so it is the full pipeline with scout-implementer.", true)]
    [InlineData("not-fast-path.md", "Bug fix lane (bugfix.md). Not a fast path: the change touches the provider's streaming path, so it is the full pipeline with scout-implementer.", true)]
    [InlineData("not-fast-path.md", "Classification: Bug, one line in the provider, which isn't on the 'never fast path' list.", false)]
    [InlineData("not-fast-path.md", "Classification: Bug. Shape: full pipeline, never fast path: it changes a provider call path. `performance` is not in the trigger list for this change.", true)]
    public void TheBugfixGradersAcceptTheClassificationAndRejectItsDenial(string grader, string answer, bool passes) =>
        Grader(BugfixGraders + grader).IsMatch(answer).ShouldBe(passes,
            $"{grader} must {(passes ? "accept" : "reject")}: {answer}");

    [Theory]
    [InlineData("fast-path.md", "Classification: fast path. A typo in a context folder page is doc text only, so gates 3 and 4 are N/A.", true)]
    [InlineData("fast-path.md", "Shape: **fast path**: one word in a Markdown page, no executable surface.", true)]
    [InlineData("fast-path.md", "This is not a fast path; run the full pipeline.", false)]
    [InlineData("fast-path.md", "The fast path does not apply here, so it goes through the full pipeline.", false)]
    [InlineData("fast-path.md", "Classification: documentation. Full pipeline, with every gate.", false)]
    public void TheDocOnlyGraderAcceptsTheFastPathAndRejectsItsDenial(string grader, string answer, bool passes) =>
        Grader(DocOnlyGraders + grader).IsMatch(answer).ShouldBe(passes,
            $"{grader} must {(passes ? "accept" : "reject")}: {answer}");

    [Theory]
    [InlineData("every-test-project.md", ContractsSurface, true)]
    [InlineData("every-test-project.md", "Gate 4: **every** test project under `tests/` (not `tests/old`), plus the four ui checks.", true)]
    [InlineData("every-test-project.md", "Do not run every test project; the ControlPlane tests are enough.", false)]
    [InlineData("every-test-project.md", "tests/* need not run.", false)]
    [InlineData("every-test-project.md", "There is no need to run all test projects.", false)]
    [InlineData("ui-surface.md", ContractsSurface, true)]
    [InlineData("ui-surface.md", "Every test project under `tests/` (not `tests/old`); in `ui/`: `bun run typecheck`, `bun run lint`, `bun run format`, `bun run test`.", true)]
    [InlineData("ui-surface.md", "Run every test project. bun run typecheck, bun run lint, bun run format and bun run test are not needed.", false)]
    [InlineData("ui-surface.md", "Run every test project, and do not skip the ui checks: bun run typecheck, bun run lint, bun run format, bun run test.", true)]
    [InlineData("ui-surface.md", "Run every test project; no ui/ checks are needed.", false)]
    [InlineData("every-test-project.md", "Run every test project. Test projects do not need a --filter.", true)]
    [InlineData("every-test-project.md", "Running every test project is unnecessary here.", false)]
    [InlineData("every-test-project.md", "Running all test projects is not required.", false)]
    [InlineData("every-test-project.md", "You do not have to run every test project.", false)]
    [InlineData("every-test-project.md", "There is no reason to run all test projects.", false)]
    [InlineData("ui-surface.md", "Run every test project under tests/, and in ui/ run bun run typecheck, bun run lint, bun run format and bun run test. A bun run build is not needed.", true)]
    [InlineData("ui-surface.md", "Run every test project under tests/, and in ui/ run bun run typecheck, bun run lint, bun run format and bun run test. (the Playwright e2e is not needed)", true)]
    [InlineData("ui-surface.md", "Run every test project under tests/, and in ui/ run bun run typecheck, bun run lint, bun run format and bun run test. There is no ui build.", true)]
    [InlineData("ui-surface.md", "Run every test project under tests/, and in ui/ run bun run typecheck, bun run lint, bun run format and bun run test. Run the unit checks, not ui e2e.", true)]
    [InlineData("every-test-project.md", ContractsRealAnswerOne, true)]
    [InlineData("ui-surface.md", ContractsRealAnswerOne, true)]
    [InlineData("every-test-project.md", ContractsRealAnswerTwo, true)]
    [InlineData("ui-surface.md", ContractsRealAnswerTwo, true)]
    [InlineData("ui-surface.md", "Run bun run typecheck, bun run lint, bun run format, bun run test. We do not run bun run test:e2e.", true)]
    [InlineData("every-test-project.md", "**1. Every .NET test project under `tests/`, except `tests/old`**, each with N passed and Skipped 0.", true)]
    [InlineData("every-test-project.md", "Do not run every .NET test project; the control-plane tests are enough.", false)]
    public void TheContractsSurfaceGradersAcceptTheWideSurfaceAndRejectItsDenial(string grader, string answer, bool passes) =>
        Grader(ContractsGraders + grader).IsMatch(answer).ShouldBe(passes,
            $"{grader} must {(passes ? "accept" : "reject")}: {answer}");

    [Theory]
    [InlineData("executive-write.md", OutboundAnswer, true)]
    [InlineData("read-only-lens.md", OutboundAnswer, true)]
    [InlineData("not-fast-path.md", OutboundAnswer, true)]
    [InlineData("executive-write.md", OutboundAnswerSayingNotReadOnly, true)]
    [InlineData("read-only-lens.md", OutboundAnswerSayingNotReadOnly, true)]
    [InlineData("not-fast-path.md", OutboundAnswerSayingNotReadOnly, true)]
    [InlineData("executive-write.md", "Rule A: a call is descriptive (a read) or executive (a write); adding a label is executive.", true)]
    [InlineData("executive-write.md", "The label write is descriptive, so no read-only lens is needed.", false)]
    [InlineData("executive-write.md", "This is not an executive write.", false)]
    [InlineData("read-only-lens.md", "The label write is descriptive, so no read-only lens is needed.", false)]
    [InlineData("read-only-lens.md", "Lenses: invariants and boundaries; the read-only lens is not needed here.", false)]
    [InlineData("read-only-lens.md", "Lenses: invariants and boundaries. The plugin is no longer read-only.", false)]
    [InlineData("read-only-lens.md", "Gate 6, from its table. `invariants` always. `read-only`: it adds a call leaving the process.", true)]
    [InlineData("read-only-lens.md", "Lenses: invariants, read-only and boundaries; never skip the read-only lens, because without the read-only lens a write would pass unreviewed.", true)]
    [InlineData("not-fast-path.md", "It can take the fast path; the full pipeline is not needed.", false)]
    [InlineData("not-fast-path.md", "Shape: fast path, the full pipeline is unneeded.", false)]
    [InlineData("not-fast-path.md", "Shape: fast path. Full pipeline would be disproportionate.", false)]
    [InlineData("not-fast-path.md", "Shape: fast path; a full pipeline is unnecessary.", false)]
    [InlineData("not-fast-path.md", "Full pipeline: not required for this change.", false)]
    [InlineData("not-fast-path.md", "Shape: full pipeline. The full pipeline is never optional for an outbound call.", true)]
    [InlineData("not-fast-path.md", "the full pipeline does not allow the fast path", true)]
    [InlineData("not-fast-path.md", "the full pipeline is not skipped for a one-liner", true)]
    [InlineData("executive-write.md", "A comment on the issue is descriptive and passes without approval, but adding a label is an executive write (Rule A).", true)]
    [InlineData("executive-write.md", "This is an executive write, which does not apply to descriptive output", true)]
    [InlineData("executive-write.md", "Executive writes do not apply here.", false)]
    [InlineData("executive-write.md", "Label is a descriptive write, so there is no executive concern.", false)]
    [InlineData("read-only-lens.md", "Lenses: invariants, read-only, boundaries. No read-only exemption exists for a one-line call.", true)]
    [InlineData("read-only-lens.md", "Lenses: invariants, read-only, boundaries. There is no read-only guard bypass.", true)]
    [InlineData("read-only-lens.md", "A read-only lens would be overkill.", false)]
    [InlineData("read-only-lens.md", "Lenses: invariants and boundaries; the read-only lens is unnecessary.", false)]
    [InlineData("read-only-lens.md", "Lenses: invariants and boundaries. We can skip the read-only lens.", false)]
    [InlineData("executive-write.md", "Adding a label is a write to GitHub (Rule A): the feature declares it and the owner grants it.", true)]
    [InlineData("executive-write.md", "The new call writes to the issue, so Rule A applies to it.", true)]
    [InlineData("executive-write.md", "Rule A: this is not a write, the label only describes the issue.", false)]
    [InlineData("executive-write.md", OutboundRealAnswerOne, true)]
    [InlineData("read-only-lens.md", OutboundRealAnswerOne, true)]
    [InlineData("not-fast-path.md", OutboundRealAnswerOne, true)]
    [InlineData("executive-write.md", OutboundRealAnswerTwo, true)]
    [InlineData("read-only-lens.md", OutboundRealAnswerTwo, true)]
    [InlineData("not-fast-path.md", OutboundRealAnswerTwo, true)]
    [InlineData("executive-write.md", "Posting the summary comment? It is descriptive. Adding a label is an executive write under Rule A.", true)]
    [InlineData("executive-write.md", "The existing call is descriptive; this new call is a write, so Rule A applies and the feature must declare it.", true)]
    [InlineData("executive-write.md", "This is not a write to the host filesystem; it is an executive write to GitHub.", true)]
    [InlineData("executive-write.md", "Per ADR 0009 the label call is classified as a write.", true)]
    [InlineData("executive-write.md", "Rule A (declared writes) covers this call.", true)]
    [InlineData("executive-write.md", "The label is a descriptive write.", false)]
    [InlineData("executive-write.md", "This is not a write to GitHub.", false)]
    [InlineData("read-only-lens.md", "Gate 6: invariants, read-only, boundaries.", true)]
    [InlineData("read-only-lens.md", "Gate 6 runs invariants and read-only (write contract) and boundaries.", true)]
    [InlineData("not-fast-path.md", "Classification: Feature. It is one line on the client we already have, and it isn't on the 'never fast path' list.", false)]
    [InlineData("not-fast-path.md", "Classification: Feature. The label call is not in the never-trivial list.", false)]
    [InlineData("not-fast-path.md", OutboundAnswer + " `performance` is not in the trigger list for this change.", true)]
    public void TheOutboundCallGradersAcceptTheSafeguardsAndRejectTheirDenial(string grader, string answer, bool passes) =>
        Grader(OutboundGraders + grader).IsMatch(answer).ShouldBe(passes,
            $"{grader} must {(passes ? "accept" : "reject")}: {answer}");
}
