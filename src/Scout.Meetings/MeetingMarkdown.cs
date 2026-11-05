using System.Globalization;
using System.Text;
using Scout.Conversation.Abstractions;

namespace Scout.Meetings;

/// <summary>
/// Renders a kept meeting as one Markdown document: the title, the start and end, the transcript with each line's
/// time, speaker and translation, and each question with its answer and what it carried. Times are written in the
/// offset each one carries, and lines end in <c>\n</c>, so the same meeting always renders the same text.
/// </summary>
public static class MeetingMarkdown
{
    /// <summary>The Markdown document of <paramref name="meeting"/>.</summary>
    public static string Render(MeetingRecord meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var markdown = new StringBuilder();
        markdown.Append("# ").Append(OneLine(meeting.Title)).Append('\n').Append('\n');
        markdown.Append("- Started: ").Append(Date(meeting.StartedAt)).Append('\n');
        markdown.Append("- Ended: ").Append(Date(meeting.EndedAt)).Append('\n');

        markdown.Append('\n').Append("## Transcript").Append('\n').Append('\n');
        if (meeting.Lines.Count == 0)
        {
            markdown.Append("No transcript lines.").Append('\n');
        }
        foreach (var line in meeting.Lines)
        {
            markdown.Append("- [").Append(Time(line.At)).Append("] ")
                .Append(line.Lane == "me" ? "You" : "Others").Append(": ")
                .Append(OneLine(line.Text)).Append('\n');
            if (!string.IsNullOrWhiteSpace(line.Translation))
            {
                markdown.Append("  - ");
                if (!string.IsNullOrWhiteSpace(line.Language))
                {
                    markdown.Append('(').Append(OneLine(line.Language)).Append(") ");
                }
                markdown.Append(OneLine(line.Translation)).Append('\n');
            }
        }

        markdown.Append('\n').Append("## Questions").Append('\n');
        if (meeting.Turns.Count == 0)
        {
            markdown.Append('\n').Append("No questions.").Append('\n');
        }
        foreach (var turn in meeting.Turns)
        {
            markdown.Append('\n').Append("### [").Append(Time(turn.At)).Append("] ").Append(OneLine(turn.Question)).Append('\n');
            var carried = Carried(turn);
            if (carried.Length > 0)
            {
                markdown.Append('\n').Append("_With ").Append(carried).Append("_").Append('\n');
            }
            markdown.Append('\n').Append(turn.Answer.ReplaceLineEndings("\n").TrimEnd('\n')).Append('\n');
        }
        return markdown.ToString();
    }

    private static string Carried(MeetingTurn turn)
    {
        List<string> carried = [];
        if (turn.Screenshot)
        {
            carried.Add("screenshot");
        }
        if (turn.Transcript)
        {
            carried.Add("transcript");
        }
        if (turn.ScreenText)
        {
            carried.Add("screen text");
        }
        return string.Join(", ", carried);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Time(DateTimeOffset at) => at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
