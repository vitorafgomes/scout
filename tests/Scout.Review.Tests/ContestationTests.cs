using System.Text.Json;

namespace Scout.Review.Tests;

/// <summary>The rules of Scout's second reading: its answer checked by code, what an answer does to a finding, the groups and the notes.</summary>
public sealed class ContestationTests
{
    private static Finding F(string id, string severity = Severities.Block, string path = "src/Queues.cs", string title = "Null lookup dereferenced") =>
        new(id, severity, title, path, 10, null, "why", "fix", null, "var q = Find(name);");

    private static readonly string[] Ids = ["F1", "F2", "F3"];

    [Fact]
    public void A_valid_answer_gives_each_id_its_outcome()
    {
        const string Answer = """[{"id":"F1","status":"refuted","reason":"Refuse never returns."},{"id":"F2","status":"holds","reason":"The lookup is not checked.","scenario":"A missing name reaches Peek at Queue.cs:12 and throws."},{"id":"F3","status":"uncertain","reason":"The caller is not shown."}]""";

        Contestation.Read(Answer, Ids).ShouldBe(
            new Dictionary<string, ContestOutcome>
            {
                ["F1"] = new(ContestStatus.Refuted, "Refuse never returns."),
                ["F2"] = new(ContestStatus.Holds, "The lookup is not checked.", Scenario: "A missing name reaches Peek at Queue.cs:12 and throws."),
                ["F3"] = new(ContestStatus.Uncertain, "The caller is not shown."),
            },
            "each id gets the status and reason of its one entry");
    }

    [Theory]
    [InlineData("missing", """[{"id":"F1","status":"refuted","reason":"r"}]""")]
    [InlineData("duplicated", """[{"id":"F1","status":"refuted","reason":"r"},{"id":"F2","status":"refuted","reason":"a"},{"id":"F2","status":"holds","reason":"b"}]""")]
    [InlineData("an unknown status", """[{"id":"F1","status":"refuted","reason":"r"},{"id":"F2","status":"false","reason":"r"}]""")]
    [InlineData("no reason", """[{"id":"F1","status":"refuted","reason":"r"},{"id":"F2","status":"refuted"}]""")]
    [InlineData("with no status", """[{"id":"F1","status":"refuted","reason":"r"},{"id":"F2","reason":"r"}]""")]
    public void An_entry_missing_duplicated_or_malformed_makes_its_finding_uncertain_never_refuted(string how, string answer)
    {
        var outcomes = Contestation.Read(answer, Ids).ShouldNotBeNull();

        outcomes["F2"].ShouldBe(new ContestOutcome(ContestStatus.Uncertain, Contestation.NoValidEntry), $"an entry {how} is uncertain, never refuted");
        outcomes["F1"].Status.ShouldBe(ContestStatus.Refuted, "the valid entry beside it still counts");
    }

    [Fact]
    public void An_entry_for_an_unknown_id_is_ignored()
    {
        var outcomes = Contestation.Read("""[{"id":"F9","status":"refuted","reason":"r"}]""", ["F1"]).ShouldNotBeNull();

        outcomes.ShouldBe(new Dictionary<string, ContestOutcome> { ["F1"] = new(ContestStatus.Uncertain, Contestation.NoValidEntry) }, "another id refutes nothing");
    }

    [Theory]
    [InlineData("I think they are all false.")]
    [InlineData("""{"id":"F1","status":"refuted","reason":"r"}""")]
    [InlineData("")]
    [InlineData(null)]
    public void An_answer_that_is_not_one_array_is_unreadable(string? answer) =>
        Contestation.Read(answer, Ids).ShouldBeNull("nothing of an answer that is not one JSON array is used");

    [Fact]
    public void An_array_in_a_code_fence_is_read() =>
        Contestation.Read("```json\n[{\"id\":\"F1\",\"status\":\"holds\",\"reason\":\"r\",\"scenario\":\"s\"}]\n```", ["F1"]).ShouldNotBeNull()["F1"].Status.ShouldBe(ContestStatus.Holds);

    [Theory]
    [InlineData(343)]
    [InlineData(344)]
    public void L1_a_reason_or_a_scenario_over_its_bound_is_cut_on_a_word_and_the_entry_is_kept(int length)
    {
        var reason = string.Concat(Enumerable.Repeat("Guard.cs:6 documents it. ", 20))[..length];
        var answer = JsonSerializer.Serialize(new[]
        {
            new { id = "F1", status = "refuted", reason, scenario = (string?)null },
            new { id = "F2", status = "holds", reason = "r", scenario = (string?)reason },
        });

        var outcomes = Contestation.Read(answer, ["F1", "F2"]).ShouldNotBeNull();

        outcomes["F1"].Status.ShouldBe(ContestStatus.Refuted, "a refutation is kept whatever the length of its reason");
        outcomes["F1"].Reason.ShouldSatisfyAllConditions(
            "the reason is cut at the bound on a word and ends with ...",
            r => r.Length.ShouldBeLessThanOrEqualTo(Contestation.MaxReasonLength),
            r => r.ShouldEndWith("...", Case.Sensitive),
            r => reason[r.Length - 3].ShouldBe(' ', "the cut falls between two words"),
            r => reason.ShouldStartWith(r[..^3], Case.Sensitive));
        outcomes["F2"].Scenario.ShouldNotBeNull().Length.ShouldBeLessThanOrEqualTo(Contestation.MaxReasonLength, "the scenario is cut the same way");
        Contestation.Apply(F("F1"), outcomes["F1"]).ShouldBeNull("the cut refutation is applied");
    }

    [Fact]
    public void A2_a_finding_said_to_hold_without_a_scenario_is_uncertain()
    {
        const string Answer = """[{"id":"F1","status":"holds","reason":"The lookup is not checked.","severity":"block","because":"lost or corrupted data"},{"id":"F2","status":"holds","reason":"r","scenario":"  "}]""";

        Contestation.Read(Answer, ["F1", "F2"]).ShouldBe(
            new Dictionary<string, ContestOutcome>
            {
                ["F1"] = new(ContestStatus.Uncertain, Contestation.NoScenario),
                ["F2"] = new(ContestStatus.Uncertain, Contestation.NoScenario),
            },
            "holds needs a scenario; without one, or with a blank one, the finding is uncertain");
    }

    [Theory]
    [InlineData("block", "a security or secret exposure", Severities.Block, "a block with one of the block reasons")]
    [InlineData("block", "a wrong result or a missing protection", Severities.Fix, "a block without a block reason is a fix")]
    [InlineData("block", null, Severities.Fix, "a block with no reason is a fix")]
    [InlineData("nit", "wording, documentation or style", Severities.Nit, "a lowering")]
    [InlineData("severe", "lost or corrupted data", null, "an unknown severity keeps the finding's, a fix")]
    public void A3_the_severity_the_second_reading_concludes_replaces_the_first_reading_s_and_block_needs_a_named_reason(string severity, string? because, string? concluded, string why)
    {
        var answer = JsonSerializer.Serialize(new[] { new { id = "F1", status = "holds", reason = "r", scenario = "s", severity, because } });

        var outcome = Contestation.Read(answer, ["F1"]).ShouldNotBeNull()["F1"];

        outcome.Severity.ShouldBe(concluded, why);
        Contestation.Apply(F("F1", Severities.Fix), outcome).ShouldNotBeNull().Severity.ShouldBe(concluded ?? Severities.Fix, $"{why}: the held finding takes it");
    }

    [Fact]
    public void A_held_block_whose_answer_gives_no_known_severity_is_capped_at_fix()
    {
        Contestation.Apply(F("F1", Severities.Block), new(ContestStatus.Holds, "r", Scenario: "s")).ShouldNotBeNull().Severity
            .ShouldBe(Severities.Fix, "a block must be concluded by the second reading with its reason");
        Contestation.Apply(F("F1", Severities.Nit), new(ContestStatus.Holds, "r", Scenario: "s")).ShouldNotBeNull().Severity
            .ShouldBe(Severities.Nit, "a severity that does not block is kept");
    }

    [Fact]
    public void A4_a_held_finding_s_why_ends_with_its_scenario_within_the_bound_of_why()
    {
        var held = Contestation.Apply(F("F1"), new(ContestStatus.Holds, "r", Scenario: "A missing name reaches Peek and throws."));
        var long_ = Contestation.WithScenario(new string('w', FindingLimits.MaxTextLength), "A missing name reaches Peek and throws.");

        held.ShouldNotBeNull().Why.ShouldEndWith(" Scenario: A missing name reaches Peek and throws.", Case.Sensitive, "the scenario is the why's last sentence");
        long_.Length.ShouldBe(FindingLimits.MaxTextLength, "the why keeps its bound");
        long_.ShouldEndWith("... Scenario: A missing name reaches Peek and throws.", Case.Sensitive, "the why before the scenario is cut, never the scenario");
    }

    [Fact]
    public void A3_the_rule_s_text_names_every_block_reason_once()
    {
        SeverityRule.BlockReasons.ShouldAllBe(r => SeverityRule.Text.Contains($"\"{r}\"", StringComparison.Ordinal), "the instructions give the reasons code checks");
        ContestRequest.Instructions.ShouldContain(SeverityRule.Text, Case.Sensitive, "the instructions carry the rule");
    }

    [Fact]
    public void K1_a_refuted_finding_is_not_kept_and_K2_an_uncertain_one_is_kept_never_blocking()
    {
        Contestation.Apply(F("F1"), new(ContestStatus.Refuted, "r")).ShouldBeNull("a refuted finding is not on the card");
        Contestation.Apply(F("F1"), new(ContestStatus.Uncertain, "r")).ShouldBe(
            F("F1", Severities.Fix, title: "Unconfirmed: Null lookup dereferenced"), "an uncertain blocking finding becomes fix and says it is unconfirmed");
        Contestation.Apply(F("F1", Severities.Nit), new(ContestStatus.Uncertain, "r")).ShouldNotBeNull().Severity.ShouldBe(Severities.Nit, "a severity that does not block is kept");
        Contestation.Apply(F("F1"), new(ContestStatus.Holds, "r", Severities.Block)).ShouldBe(F("F1"), "a finding that holds with its block concluded is kept as it is");
        Contestation.Apply(F("F1"), null).ShouldBe(F("F1"), "a finding not contested is kept as it is");
    }

    [Fact]
    public void An_uncertain_finding_is_prefixed_once_and_its_title_stays_within_its_bound()
    {
        var once = Contestation.Apply(F("F1"), new(ContestStatus.Uncertain, "r")).ShouldNotBeNull();
        Contestation.Apply(once, new(ContestStatus.Uncertain, "r")).ShouldBe(once, "applying the outcome again changes nothing");
        Contestation.Apply(F("F1", title: new string('t', FindingLimits.MaxTitleLength)), new(ContestStatus.Uncertain, "r")).ShouldNotBeNull()
            .Title.Length.ShouldBe(FindingLimits.MaxTitleLength, "the prefixed title is cut to its bound");
    }

    [Fact]
    public void Groups_keep_a_file_s_findings_together_hold_at_most_four_and_close_before_the_bound()
    {
        var findings = new[]
        {
            F("A1", path: "a.cs"), F("B1", path: "b.cs"), F("A2", path: "a.cs"), F("A3", path: "a.cs"), F("A4", path: "a.cs"), F("A5", path: "a.cs"), F("C1", path: "c.cs"), F("D1", path: "d.cs"),
        };

        var groups = Contestation.Groups(findings, f => f.Id == "C1" ? 95 : 10, 100);

        groups.Select(g => string.Join(",", g.Select(f => f.Id))).ShouldBe(
            ["A1,A2,A3,A4", "A5,B1", "C1", "D1"], "a file's findings together where it first appears, four at most, and a group closed before the bound");
    }

    [Fact]
    public void The_notes_name_each_refuting_group_s_first_finding_and_reason_count_the_rest_and_say_what_was_not_confirmed_or_not_contested()
    {
        var notes = Contestation.Notes(
            [(3, "Null lookup dereferenced", "Refuse is marked as never returning."), (0, "x", "y"), (1, "T2", "R2"), (1, "T3", "R3"), (2, "T4", "R4"), (1, "T5", "R5")],
            2,
            [(5, "the review reached its 15 groups."), (4, "the answer could not be read.")]);

        notes.ShouldBe(
            [
                "3 findings refuted by Scout's second reading: Null lookup dereferenced: Refuse is marked as never returning.",
                "1 finding refuted by Scout's second reading: T2: R2",
                "1 finding refuted by Scout's second reading: T3: R3",
                "2 more groups of findings had findings refuted by Scout's second reading.",
                "2 findings could not be confirmed by Scout's second reading; they are kept as unconfirmed and do not block.",
                "Scout's second reading did not run for 5 findings: the review reached its 15 groups.",
                "Scout's second reading did not run for 4 findings: the answer could not be read.",
            ],
            "one line per refuting group up to three, a closing count, the unconfirmed count and each reason it did not run");
        Contestation.Notes([], 0, []).ShouldBeEmpty("a second reading that changed nothing adds no note");
    }

    [Fact]
    public void A_card_holds_at_most_twenty_notes_and_says_how_many_more()
    {
        var notes = Enumerable.Range(1, 25).Select(n => $"n{n}").ToList();

        ReviewNotes.Fit(notes).ShouldBe([.. notes.Take(19), "6 more notes are not shown."], "twenty notes at most, the last counting the rest");
        ReviewNotes.Fit(notes[..20]).ShouldBe(notes[..20], "twenty notes are all shown");
        ReviewNotes.Cut(new string('n', 400)).ShouldBe(new string('n', 297) + "...", "a note is cut to its bound");
    }

    [Fact]
    public void A_first_reading_s_title_never_keeps_the_unconfirmed_prefix_and_the_second_reading_s_mark_is_set_once()
    {
        Contestation.FirstReading(F("F1", title: "Unconfirmed: unconfirmed:Null lookup")).Title.ShouldBe("Null lookup", "any leading prefix, in any case, is removed");
        Contestation.FirstReading(F("F1")).ShouldBe(F("F1"), "a title without it is kept as it is");
        Contestation.UnconfirmedTitle(Contestation.UnconfirmedTitle("T")).ShouldBe("Unconfirmed: T", "the mark is set once");
    }

    [Fact]
    public void An_entry_decides_only_the_finding_whose_id_it_names_exactly()
    {
        var outcomes = Contestation.Read("""[{"id":"a","status":"refuted","reason":"r"}]""", ["a", "A"]).ShouldNotBeNull();

        outcomes["a"].Status.ShouldBe(ContestStatus.Refuted, "the entry decides the finding it names");
        outcomes["A"].ShouldBe(new ContestOutcome(ContestStatus.Uncertain, Contestation.NoValidEntry), "an id that differs only in case is another finding");
    }
}
