using Scout.Conversation.Abstractions;

namespace Scout.Meetings.Tests;

public sealed class MeetingMarkdownTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Render_Is_Deterministic_With_Transcript_Translations_And_Question_Flags()
    {
        var meeting = new MeetingRecord(
            "abc",
            Start,
            Start.AddMinutes(45),
            "Weekly sync",
            [
                new MeetingLine(Start.AddSeconds(5), "me", "Can we ship on Friday?", null, null),
                new MeetingLine(Start.AddSeconds(9), "others", "Sim, se os testes\npassarem.", "Yes, if the tests pass.", "en"),
            ],
            [
                new MeetingTurn(Start.AddMinutes(2), "What did they promise?", "Friday, if the tests pass.", false, true, false),
                new MeetingTurn(Start.AddMinutes(4), "What is on the slide?", "The release checklist:\r\n- tests\n- notes\n", true, true, true),
                new MeetingTurn(Start.AddMinutes(6), "Anything else?", "No.", false, false, false),
            ]);

        var first = MeetingMarkdown.Render(meeting);
        var second = MeetingMarkdown.Render(meeting with { Lines = [.. meeting.Lines], Turns = [.. meeting.Turns] });

        first.ShouldBe(
            """
            # Weekly sync

            - Started: 2026-11-04 10:00:00 +01:00
            - Ended: 2026-11-04 10:45:00 +01:00

            ## Transcript

            - [10:00:05] You: Can we ship on Friday?
            - [10:00:09] Others: Sim, se os testes passarem.
              - (en) Yes, if the tests pass.

            ## Questions

            ### [10:02:00] What did they promise?

            _With transcript_

            Friday, if the tests pass.

            ### [10:04:00] What is on the slide?

            _With screenshot, transcript, screen text_

            The release checklist:
            - tests
            - notes

            ### [10:06:00] Anything else?

            No.

            """.ReplaceLineEndings("\n"));
        second.ShouldBe(first, "the same meeting always renders the same document");
    }

    [Fact]
    public void A_Meeting_Without_Lines_Or_Questions_Says_So()
    {
        var markdown = MeetingMarkdown.Render(new MeetingRecord(null, Start, Start, "Empty", [], []));

        markdown.ShouldContain("## Transcript\n\nNo transcript lines.\n");
        markdown.ShouldEndWith("## Questions\n\nNo questions.\n");
    }
}
