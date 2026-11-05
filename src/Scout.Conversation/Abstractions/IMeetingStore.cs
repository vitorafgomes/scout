namespace Scout.Conversation.Abstractions;

/// <summary>
/// The meetings the owner chose to keep on this machine. Registered only when the meetings database is configured;
/// the local adapter answers every meeting request with a sentence saying so when none is. Nothing it holds leaves the process.
/// </summary>
public interface IMeetingStore
{
    /// <summary>
    /// Stores <paramref name="meeting"/> under a new id, ignoring <see cref="MeetingRecord.Id"/>, and answers that id;
    /// throws <see cref="ArgumentException"/>, storing nothing, when the meeting is past the limits.
    /// </summary>
    Task<string> KeepAsync(MeetingRecord meeting, CancellationToken ct);

    /// <summary>At most <paramref name="limit"/> kept meetings, newest first.</summary>
    Task<IReadOnlyList<MeetingSummary>> ListAsync(int limit, CancellationToken ct);

    /// <summary>The kept meeting with <paramref name="id"/>, or null when there is none.</summary>
    Task<MeetingRecord?> GetAsync(string id, CancellationToken ct);

    /// <summary>Deletes the meeting with <paramref name="id"/>, its lines and its turns; false when there is none.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);

    /// <summary>Deletes every kept meeting.</summary>
    Task DeleteAllAsync(CancellationToken ct);
}

/// <summary>A kept meeting: its transcript lines and the questions the owner asked during it.</summary>
public sealed record MeetingRecord(
    string? Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string Title,
    IReadOnlyList<MeetingLine> Lines,
    IReadOnlyList<MeetingTurn> Turns);

/// <summary>One final transcript line of a lane (<c>me</c> or <c>others</c>), with its translation when there was one.</summary>
public sealed record MeetingLine(DateTimeOffset At, string Lane, string Text, string? Translation, string? Language);

/// <summary>One question the owner asked during the meeting, its answer, and what the question carried.</summary>
public sealed record MeetingTurn(DateTimeOffset At, string Question, string Answer, bool Screenshot, bool Transcript, bool ScreenText);

/// <summary>A kept meeting in a list: its id, times, title and how many lines and turns it holds.</summary>
public sealed record MeetingSummary(
    string Id, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Title, int LineCount, int TurnCount);
