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
            [(5, "the review reached its 15 groups.", []), (4, "the answer could not be read.", ["Lock held across an await", "Name the constant"])]);

        notes.ShouldBe(
            [
                "3 findings refuted by Scout's second reading: Null lookup dereferenced: Refuse is marked as never returning.",
                "1 finding refuted by Scout's second reading: T2: R2",
                "1 finding refuted by Scout's second reading: T3: R3",
                "2 more groups of findings had findings refuted by Scout's second reading.",
                "2 findings could not be confirmed by Scout's second reading; they are kept as unconfirmed and do not block.",
                "Scout's second reading did not run for 5 findings: the review reached its 15 groups.",
                "Scout's second reading did not run for 4 findings: the answer could not be read. Kept as the first reading gave them: Lock held across an await; Name the constant.",
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

    [Fact]
    public void T1_a_finding_true_and_recorded_by_the_project_as_later_work_is_kept_as_a_note_at_the_lowest_severity_titled_and_with_its_record()
    {
        var read = Contestation.Read("""[{"id":"F1","status":"noted","reason":"docs/followups.md:12 records the retry as later work."}]""", ["F1"]).ShouldNotBeNull();

        var kept = Contestation.Apply(F("F1"), read["F1"]).ShouldNotBeNull("a noted finding is kept on the card");

        read["F1"].ShouldBe(new ContestOutcome(ContestStatus.Noted, "docs/followups.md:12 records the retry as later work."), "noted is an answer of its own, with its reason");
        kept.ShouldBe(
            F("F1", Severities.Nit, title: "Noted: Null lookup dereferenced") with { Why = "why Recorded by the project: docs/followups.md:12 records the retry as later work." },
            "a noted finding is a nit whatever it was, titled Noted, its why ending with the record");
        Contestation.Apply(kept, read["F1"]).ShouldBe(kept, "applying the outcome again changes nothing");
        ContestStatus.StatusOf(ContestStatus.StateOf(ContestStatus.Noted)).ShouldBe(ContestStatus.Noted, "noted is recorded as its own state and read back as noted");
        Contestation.Notes([], 0, [], 0, 2).ShouldBe(["2 findings are true and already recorded by the project as later work; they are kept as notes."], "the card counts the notes");
        Contestation.Notes([], 0, [], 0, 1).ShouldBe(["1 finding is true and already recorded by the project as later work; it is kept as a note."], "one note is counted in the singular");
    }

    [Fact]
    public void T1_a_first_reading_s_title_never_keeps_the_noted_prefix_so_only_the_second_reading_sets_it()
    {
        Contestation.FirstReading(F("F1", title: "noted: Unconfirmed: NOTED:Null lookup")).Title.ShouldBe("Null lookup", "every leading prefix, in any case and any order, is removed");
        Contestation.NotedTitle(Contestation.NotedTitle("T")).ShouldBe("Noted: T", "the mark is set once");
    }

    [Fact]
    public void T1_the_instructions_separate_noted_from_refuted_and_keep_a_recorded_data_loss_or_secret_exposure_at_its_severity()
    {
        ContestRequest.Instructions.ShouldContain("intended and correct as designed is refuted", Case.Sensitive, "refuted is for behaviour that is the design");
        ContestRequest.Instructions.ShouldContain("A real shortcoming the project has written down as not done yet is noted, not refuted", Case.Sensitive, "noted is for a real shortcoming the project recorded");
        ContestRequest.Instructions.ShouldContain("a known weak point the pull request's own description lists", Case.Sensitive, "a weak point the description lists is noted too");
        ContestRequest.Instructions.ShouldContain(
            "Writing a problem down does not make it safe to ship: a recorded shortcoming that silently loses or corrupts data, or exposes a secret, holds at its severity, and the reason cites the record.",
            Case.Sensitive, "a recorded data loss or secret exposure is not lowered to a note");
        ContestRequest.Instructions.ShouldContain("\"holds\", \"refuted\", \"noted\" or \"uncertain\"", Case.Sensitive, "the answer may say noted");
    }

    private const string Monitoring = "web/src/app/monitoring/monitoring.component.ts";

    private static readonly string[] Changed = [Monitoring, "web/src/app/queues/queue-list.component.ts", "api/Queues.cs", "api/v2/Queues.cs"];

    private static int? Lines(string path) => path switch
    {
        Monitoring => 300,
        "web/src/app/queues/queue-list.component.ts" => 200,
        "api/Queues.cs" => 50,
        "api/v2/Queues.cs" => 60,
        "docs/followups.md" => 40,
        "src/Queues.cs" => 20,
        _ => null,
    };

    private static ContestOutcome Check(string status, string reason, string findingPath = Monitoring) =>
        Contestation.Checked(new ContestOutcome(status, reason), findingPath, Changed, Lines);

    [Theory]
    [InlineData("monitoring.component.ts:127 checks the value before it is read.", "the finding's own file by its name alone", Monitoring)]
    [InlineData("monitoring/monitoring.component.ts line 127 checks it.", "the finding's own file by a trailing part of its path", Monitoring)]
    [InlineData("The guard at monitoring.component.ts:131 returns first, and queue-list.component.ts lines 40-44 never pass null.", "the finding's own file and one changed file, by name", Monitoring)]
    [InlineData("queue-list.component.ts#L180 records it as later work.", "one changed file by its name alone", "web/src/app/queues/queue-list.component.ts")]
    [InlineData("Queues.cs:55 returns before the lookup.", "a name two changed files end with", null)]
    [InlineData("Elsewhere.cs:9999 handles it.", "a name no file ends with", null)]
    [InlineData("docs/missing.md:12 describes it.", "a path with folders the head does not hold", null)]
    [InlineData("web/src/app/monitoring/monitoring.component.ts:299 is the design.", "the full path at a line it holds", Monitoring)]
    public void C1_a_citation_by_a_name_that_can_only_be_one_file_is_checked_against_that_file_and_one_that_cannot_be_resolved_stands(string reason, string what, string? resolved)
    {
        Contestation.Resolve(Contestation.Cited(reason)[0].Path, Monitoring, Changed, Lines).ShouldBe(resolved, $"{what} resolves to {resolved ?? "no one file"}");
        foreach (var status in new[] { ContestStatus.Refuted, ContestStatus.Noted })
        {
            Check(status, reason).ShouldBe(new ContestOutcome(status, reason), $"{status} citing {what} stands");
        }
    }

    [Theory]
    [InlineData("monitoring.component.ts:301 checks it.", Monitoring, 301, "the finding's own file by name, past its end")]
    [InlineData("queue-list.component.ts:250 says so.", "web/src/app/queues/queue-list.component.ts", 250, "one changed file by name, past its end")]
    [InlineData("Recorded at docs/followups.md#L41.", "docs/followups.md", 41, "a full path past its end")]
    [InlineData("src/Queues.cs:8 is fine; ./docs/followups.md lines 90-95 records it.", "docs/followups.md", 90, "the second citation of two, past its end")]
    public void T3_a_refutation_or_a_note_whose_cited_line_is_past_the_end_of_the_one_file_it_names_leaves_the_finding_unconfirmed(string reason, string path, int line, string what)
    {
        foreach (var status in new[] { ContestStatus.Refuted, ContestStatus.Noted })
        {
            var checkedOutcome = Check(status, reason);

            checkedOutcome.ShouldBe(new ContestOutcome(ContestStatus.Uncertain, Contestation.CitesNothingReal(path, line)), $"{status} citing {what} is not a reading, named by the path it resolved to");
            Contestation.Apply(F("F1"), checkedOutcome).ShouldNotBeNull().Title.ShouldBe("Unconfirmed: Null lookup dereferenced", $"the finding stays, unconfirmed ({what})");
        }
    }

    [Fact]
    public void T2_a_refuted_finding_is_still_removed_when_its_cited_places_exist()
    {
        var refuted = new ContestOutcome(ContestStatus.Refuted, "The guard at src/Queues.cs:8 returns first, and docs/followups.md line 40 describes it.");

        var checkedOutcome = Contestation.Checked(refuted, "src/Queues.cs", Changed, Lines);

        checkedOutcome.ShouldBe(refuted, "a refutation whose every cited file and line exists stands");
        Contestation.Apply(F("F1"), checkedOutcome).ShouldBeNull("a refuted finding is removed from the card");
    }

    [Fact]
    public void T3_only_refutations_and_notes_are_checked_and_a_reason_that_cites_no_line_is_left_as_it_is()
    {
        var holds = new ContestOutcome(ContestStatus.Holds, "src/Gone.cs:3 throws.", Scenario: "s");
        var plain = new ContestOutcome(ContestStatus.Noted, "The pull request's description lists it as a known weak point.");

        Contestation.Checked(holds, "src/Queues.cs", Changed, _ => null).ShouldBe(holds, "a finding that holds is not checked here");
        Contestation.Checked(plain, "src/Queues.cs", Changed, _ => null).ShouldBe(plain, "a reason with no file and line has nothing to check");
        Contestation.Cited("See .github/ROADMAP.md:7, src/a.b/C.cs line 3 and v1.2 of the API.").ShouldBe([(".github/ROADMAP.md", 7), ("src/a.b/C.cs", 3)], "a path and line are read, a version number is not");
    }
}
