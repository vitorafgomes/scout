using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Every requirement the agent documents carry is still stated by the document that carries it, in the requirement's
/// own words: a rewrite of those documents may rename a rule, a gate, a file or a heading, and may not drop what they ask.
/// </summary>
public partial class HarnessRequirementTests
{
    private const string Charter = "CLAUDE.md";
    private const string Rules = "AGENTS.md";
    private const string ContextContract = "context/CLAUDE.md";
    private const string Pipeline = ".claude/skills/scout-pipeline/SKILL.md";
    private const string FeatureLane = ".claude/skills/scout-pipeline/feature.md";
    private const string BugfixLane = ".claude/skills/scout-pipeline/bugfix.md";
    private const string RefactorLane = ".claude/skills/scout-pipeline/refactor.md";
    private const string DiagnoseLane = ".claude/skills/scout-pipeline/diagnose.md";
    private const string PipelineSensitive = ".claude/skills/scout-pipeline/reference/sensitive.md";
    private const string PipelineRecords = ".claude/skills/scout-pipeline/reference/records.md";
    private const string Implementer = ".claude/agents/scout-implementer.md";
    private const string Reviewer = ".claude/agents/scout-reviewer.md";
    private const string Mechanic = ".claude/agents/scout-mechanic.md";
    private const string Tooling = "context/pages/conventions/tooling.md";
    private const string Testing = "context/pages/conventions/testing.md";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\brules?\s+[a-j]\b|\bgates?\s+\d", RegexOptions.IgnoreCase)]
    private static partial Regex RuleLetterOrGateNumber();

    /// <summary>Lower case, without Markdown emphasis or code marks, every run of whitespace one space, so a re-wrap or a bold word never reads as a removal.</summary>
    private static string Normalized(string text) =>
        Whitespace().Replace(text.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal), " ")
            .Trim()
            .ToLowerInvariant();

    [Theory]
    [InlineData("every call that leaves the process is declared, a write runs only under the owner's grant, an irreversible one only after the owner approves it, and every write is logged", Rules,
        "a feature declares each call that leaves the process and whether it writes", "a write runs only after the owner grants that feature in the desktop app",
        "an irreversible one only after the owner approves that action there", "every write is logged")]
    [InlineData("every call that leaves the process is declared, a write runs only under the owner's grant, an irreversible one only after the owner approves it, and every write is logged", Pipeline,
        "the plan also states, for every call that leaves the process, what its feature declares (read, write, irreversible write)")]
    [InlineData("every call that leaves the process is declared, a write runs only under the owner's grant, an irreversible one only after the owner approves it, and every write is logged", PipelineRecords,
        "every call leaving the process declared, granted, approved when irreversible and logged")]
    [InlineData("a function the model can invoke is judged by what it can reach, never by what its description promises", Rules,
        "a function the model can invoke is judged by what it can reach, never by what its description promises")]
    [InlineData("a function the model can invoke is judged by what it can reach, never by what its description promises", Reviewer,
        "a new function the model can invoke is judged by what it can reach, whatever its description says")]
    [InlineData("an edit ships with a test that fails without it and asserts the requirement in the request's words", Rules,
        "a change ships with its test. the test fails without the change and asserts the requirement in the words of the request")]
    [InlineData("an edit ships with a test that fails without it and asserts the requirement in the request's words", Pipeline,
        "edit, then the test for that edit, then the architecture tests, step by step", "the test fails when the behaviour is removed")]
    [InlineData("an edit ships with a test that fails without it and asserts the requirement in the request's words", Implementer,
        "make the edit, then run the test its row names, then the architecture tests", "the test asserts the requirement in the plan's own words")]
    [InlineData("an edit ships with a test that fails without it and asserts the requirement in the request's words", BugfixLane,
        "the failing test is step one of the plan")]
    [InlineData("the build has no warning and format and diagnostics are clean", Rules,
        "a change is done when the build has no warning, formatting and diagnostics are clean", "dotnet build scout.slnx (a warning is an error)",
        "dotnet format scout.slnx --verify-no-changes", "tools/checks/check-diagnostics.sh <path to .csproj>")]
    [InlineData("the build has no warning and format and diagnostics are clean", Pipeline,
        "dotnet build scout.slnx with 0 warnings.", "dotnet format scout.slnx --verify-no-changes, clean.", "tools/checks/check-diagnostics.sh <touched .csproj>, clean")]
    [InlineData("the build has no warning and format and diagnostics are clean", Implementer,
        "dotnet build scout.slnx -c debug with 0 warning(s)", "tools/checks/check-diagnostics.sh on each touched .csproj, clean")]
    [InlineData("the full test surface runs once, unfiltered and with nothing skipped, before the commit message", Pipeline,
        "a filter is for one named proof; the full surface then runs once without one", "with a count per project and 0 skipped")]
    [InlineData("the full test surface runs once, unfiltered and with nothing skipped, before the commit message", Implementer,
        "after the last row, run the whole acceptance surface once more with no filter", "record the exact totals; skipped is 0")]
    [InlineData("the full test surface runs once, unfiltered and with nothing skipped, before the commit message", Mechanic,
        "a project that exits green after executing 0 tests gets did-not-run")]
    [InlineData("the full test surface runs once, unfiltered and with nothing skipped, before the commit message", Testing,
        "one verdict per project, skipped must be 0")]
    [InlineData("a requirement is proved by a targeted mutation whose test fails on a quoted assertion, the file restored from a backup copy", Pipeline,
        "the test fails on one targeted mutation", "the mutation with the test's assertion message quoted and the file restored from a backup copy",
        "a row without proof blocks the commit message")]
    [InlineData("a requirement is proved by a targeted mutation whose test fails on a quoted assertion, the file restored from a backup copy", Implementer,
        "make one change that removes only the behaviour under test", "the test name and its assertion message, quoted",
        "a build error is not a failing test: pick another mutation", "the file goes back from your backup copy. git checkout erases uncommitted work")]
    [InlineData("a requirement is proved by a targeted mutation whose test fails on a quoted assertion, the file restored from a backup copy", Reviewer,
        "shows one mutation that made its test fail on a quoted assertion, then the file restored and the build repeated")]
    [InlineData("a requirement is proved by a targeted mutation whose test fails on a quoted assertion, the file restored from a backup copy", Tooling,
        "a targeted mutation per new behaviour entry with the failure quoted, the file restored from a backup copy and rebuilt")]
    [InlineData("no abstraction for a single user", Rules,
        "no abstraction for one user. a generic, a base class or a helper needs a second caller")]
    [InlineData("no abstraction for a single user", RefactorLane,
        "nothing gains an abstraction with one user")]
    [InlineData("what is cited or recommended is read first", Rules,
        "a file, class or convention is read in the current tree before it is recommended")]
    [InlineData("what is cited or recommended is read first", Reviewer,
        "a file:line you read in the code", "read the code at each line you will cite")]
    [InlineData("what is cited or recommended is read first", DiagnoseLane,
        "read anything you recommend in the current code first")]
    [InlineData("no commit, push, pull request, release or merge without the owner's word, and never an AI attribution line", Rules,
        "no commit, push, pull request, release or merge unless the owner asks for that action", "every change reaches main through a pull request",
        "a commit, a pull request and a source file never carry a line that credits an ai tool")]
    [InlineData("no commit, push, pull request, release or merge without the owner's word, and never an AI attribution line", Pipeline,
        "propose one conventional commit message in a code block, and stop. the owner decides about git")]
    [InlineData("no commit, push, pull request, release or merge without the owner's word, and never an AI attribution line", Implementer,
        "you run no git commit, no git push and no other command that writes history")]
    [InlineData("no commit, push, pull request, release or merge without the owner's word, and never an AI attribution line", Tooling,
        "history-writing git (commit, push, merge, rebase", "every gh command that writes to github")]
    [InlineData("no vendor type crosses a pluggable boundary and the model is reached through one path", Rules,
        "a vendor sdk type stays inside its own adapter, provider or plugin", "the model is reached through semantic kernel only")]
    [InlineData("no vendor type crosses a pluggable boundary and the model is reached through one path", PipelineSensitive,
        "a vendor sdk type in the core or in another adapter; a second path to the model")]
    [InlineData("no vendor type crosses a pluggable boundary and the model is reached through one path", Reviewer,
        "no vendor sdk type appears in scout.conversation, scout.skills, scout.controlplane.contracts or another adapter")]
    [InlineData("credentials arrive only through configuration binding and are never logged or kept in settings files", Rules,
        "credentials come from configuration binding", "appsettings*.json holds names and no values", "nothing logs a credential")]
    [InlineData("credentials arrive only through configuration binding and are never logged or kept in settings files", Reviewer,
        "a token, key or connection string enters only through configuration binding", "no log line and no error message carries a credential")]
    [InlineData("tests assert with Shouldly only", Rules,
        "tests assert with shouldly. fluentassertions is commercially licensed from version 8 and is never added")]
    [InlineData("tests assert with Shouldly only", Reviewer,
        "tests assert with shouldly;")]
    [InlineData("tests assert with Shouldly only", Testing,
        "shouldly 4.3.0 for assertions", "do not mix two assertion libraries")]
    [InlineData("every call across an external boundary ships one test where that call fails", Rules,
        "a change that adds or alters a call across http, grpc, a chat sdk, a provider, the filesystem or a subprocess ships a test in which that call fails (timeout, 5xx, reset) and asserts what the caller does")]
    [InlineData("every call across an external boundary ships one test where that call fails", Reviewer,
        "each new call across an external boundary has a test in which the call fails (timeout, 5xx, reset) and that asserts what the caller does")]
    [InlineData("every call across an external boundary ships one test where that call fails", FeatureLane,
        "ships one test in which its external call fails")]
    [InlineData("every call across an external boundary ships one test where that call fails", PipelineSensitive,
        "the happy path is tested and the timeout, 5xx or reset is not")]
    [InlineData("every call across an external boundary ships one test where that call fails", Testing,
        "one failing-call test per external boundary", "a failing-call test makes the real client library raise its real exception")]
    [InlineData("documentation lives in the context folder, in English, and code carries no narration and no documentation reference", Rules,
        "documentation lives in context/, in english, with no emojis.", "no comment that narrates inside a member or refers to a decision, a page or an issue")]
    [InlineData("documentation lives in the context folder, in English, and code carries no narration and no documentation reference", Reviewer,
        "code carries no comment narrating inside a member and no reference to documentation.")]
    [InlineData("documentation lives in the context folder, in English, and code carries no narration and no documentation reference", Testing,
        "a comment never cites a context folder path, an adr number or an issue number")]
    [InlineData("the context folder is read before the source", Charter,
        "read that page before the source, not the whole folder")]
    [InlineData("the context folder is read before the source", ContextContract,
        "before any code grep or file read", "only after the context folder is exhausted, grep source")]
    [InlineData("the context folder is read before the source", BugfixLane,
        "read. the project's rules file and the pages it names for the code the bug is in")]
    [InlineData("the context folder is read before the source", Implementer,
        "read. the rules file of the project.", "every file on the plan's read list")]
    [InlineData("the context folder is read before the source", DiagnoseLane,
        "read the project first. its rules file, the topic pages the rules file names for the code in question",
        "the worker's logs and opentelemetry output before source")]
    [InlineData("a review runs one reviewer per lens the diff names, every finding faces a challenger, a silent challenger leaves the finding open and a silent lens blocks", Pipeline,
        "one run of the scout-review workflow with the lenses for the size", "it sends one scout-reviewer per lens, then has an independent reviewer try to refute each finding",
        "unresolved (the refuter returned nothing, or could not find it) | verify it yourself; it blocks like a confirmed one until you have",
        "unresolvedlenses | run that lens again; it blocks until it returns")]
    [InlineData("a review runs one reviewer per lens the diff names, every finding faces a challenger, a silent challenger leaves the finding open and a silent lens blocks", PipelineSensitive,
        "give the review the lenses it names", "you may add a lens; you may not drop one a row names")]
    [InlineData("a review runs one reviewer per lens the diff names, every finding faces a challenger, a silent challenger leaves the finding open and a silent lens blocks", Tooling,
        "whose only job is to refute it", "a finding whose refuter returned nothing, or answered located: false, goes to unresolved", "(the gate fails closed)",
        "a lens whose review returned nothing is named in", "blocks until it is run again")]
    [InlineData("a review runs one reviewer per lens the diff names, every finding faces a challenger, a silent challenger leaves the finding open and a silent lens blocks", Reviewer,
        "read the code at each line you will cite", "the rule it breaks: a line of a rules file, a context page, or a rule in agents.md",
        "a finding you could not locate is located: false, never refuted")]
    [InlineData("two fix-and-review rounds at most, then the rest goes to the owner", Pipeline,
        "two rounds of fix and review", "a third means the change is not understood: stop and hand the rest to the owner")]
    [InlineData("one worktree per piece of work, and the owner's checkout is never touched", Charter,
        "one git worktree per piece of work running at the same time", "an agent never works in the owner's own checkout")]
    [InlineData("the work stops at a proposed commit message", Pipeline,
        "propose one conventional commit message in a code block, and stop")]
    [InlineData("the work stops at a proposed commit message", Implementer,
        "the sending session writes the commit message and decides about git")]
    [InlineData("the files that are never a fast path, whatever their extension", PipelineSensitive,
        "never fast path, whatever the extension .editorconfig, directory.build.props, directory.packages.props, global.json, scout.slnx, .github/workflows/, appsettings.json, .claude/, tools/, ui/package.json, ui/bun.lock, ui/build/.")]
    [InlineData("the files that are never a fast path, whatever their extension", Pipeline,
        "a file on the never-fast-path list in reference/sensitive.md is at least standard")]
    [InlineData("a capability is checked before it is added: a call leaving the process, a function the model can invoke, a credential path, an adapter, provider or plugin", PipelineSensitive,
        "adds or changes a call that leaves the process", "adds a function or tool the model can invoke",
        "touches credentials, tokens, auth headers, configuration binding", "adds or changes an adapter, an llm provider, a plugin registration")]
    [InlineData("a capability is checked before it is added: a call leaving the process, a function the model can invoke, a credential path, an adapter, provider or plugin", Pipeline,
        "a match in its table is sensitive however small the diff")]
    [InlineData("a capability is checked before it is added: a call leaving the process, a function the model can invoke, a credential path, an adapter, provider or plugin", FeatureLane,
        "its rules file has no rule yet for a capability it does not have")]
    [InlineData("a capability is checked before it is added: a call leaving the process, a function the model can invoke, a credential path, an adapter, provider or plugin", RefactorLane,
        "moving a call out of an abstraction, changing which project talks to a vendor sdk, or changing where a configuration value is read adds a capability")]
    public void TheAgentDocumentsStillStateEveryRequirementTheyCarry(string requirement, string document, params string[] phrases)
    {
        phrases.ShouldNotBeEmpty($"the row for \"{requirement}\" names no phrase, so it passes whatever {document} says");
        phrases.Where(phrase => RuleLetterOrGateNumber().IsMatch(phrase)).ShouldBeEmpty(
            $"the row for \"{requirement}\" matches a rule letter or a gate number, which a rename changes while the requirement stays");

        var path = Path.Combine(ArchitectureTests.SolutionRoot(), document);
        File.Exists(path).ShouldBeTrue($"{document}, which states that {requirement}, does not exist");

        var text = Normalized(File.ReadAllText(path));

        phrases.Where(phrase => text.Contains(Normalized(phrase), StringComparison.Ordinal) is false).ShouldBeEmpty(
            $"{document} no longer states that {requirement}");
    }
}
