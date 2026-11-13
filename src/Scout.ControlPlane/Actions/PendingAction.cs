using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scout.ControlPlane.Actions;

/// <summary>Where a queued action stands.</summary>
public enum PendingActionStatus
{
    /// <summary>Waiting for the owner.</summary>
    Pending,

    /// <summary>The owner approved it.</summary>
    Approved,

    /// <summary>The owner rejected it.</summary>
    Rejected,

    /// <summary>The owner approved it and the worker recorded the write; it cannot be recorded again.</summary>
    Executed,

    /// <summary>The owner approved it and a worker claimed it to run it; the claim lapses after a lease.</summary>
    Executing,
}

/// <summary>
/// An irreversible action a feature asked to perform; it runs only after the
/// owner approves it in the desktop. <c>Reason</c> carries the owner's note on the
/// decision, approval or rejection; <c>ClaimedAt</c> is when a worker last claimed it.
/// </summary>
public sealed record PendingAction(
    string Id,
    string FeatureId,
    string Action,
    string Target,
    string Summary,
    DateTimeOffset RequestedAt,
    PendingActionStatus Status,
    DateTimeOffset? DecidedAt,
    string? Reason = null,
    DateTimeOffset? ClaimedAt = null)
{
    /// <summary>The most characters a payload may hold.</summary>
    public const int MaxPayloadLength = 200_000;

    /// <summary>The payload fields an executed action keeps, what says which pull request, commit and verdict it was; the rest is dropped.</summary>
    public static readonly IReadOnlyList<string> KeptAfterExecution = ["owner", "repo", "number", "commit", "verdict"];

    /// <summary>
    /// What the worker needs to run the action, as JSON, kept with the action; never part of the summary, never logged,
    /// and handed only to the worker that claims the action. Once executed only <see cref="KeptAfterExecution"/> stays.
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>How the run that consumed the action ended, as recorded in the write log: <c>succeeded</c> or <c>failed: ...</c>.</summary>
    public string? Outcome { get; init; }

    /// <summary>What the run left for the owner to open, such as the link GitHub gave, or the sentence that says why it failed.</summary>
    public string? Result { get; init; }

    /// <summary>
    /// The request that queued the action, as the worker's write-gate request named it; null for an action queued outside
    /// a request. A group is the actions of one group id, one feature and one repository.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>The repository the action writes to, <c>owner/repo</c>, as the worker's write-gate request named it.</summary>
    public string? Repository { get; init; }

    /// <summary>When the worker reported that the request of <see cref="GroupId"/> ended; the first report is kept.</summary>
    public DateTimeOffset? GroupClosedAt { get; init; }

    /// <summary>
    /// For an issue a findings card queued: the card's id and the digest of the issue's repository, title and body, by which
    /// a later click on the same card is matched issue by issue; kept when the action is executed and its payload cut.
    /// Null otherwise.
    /// </summary>
    public string? CardKey { get; init; }

    /// <summary>The most characters of a payload's title the desktop's list carries.</summary>
    public const int MaxListedTitleLength = 256;

    /// <summary>
    /// The most characters of a payload's body the desktop's list carries: a preview; the list stays within the desktop's
    /// receive limit because the listed text of one answer is bounded in bytes too, and a row past that bound carries no
    /// text and says it was cut. The full text of one action is read on its own.
    /// </summary>
    public const int MaxListedBodyLength = 4_000;

    /// <summary>The SHA-256 digest of the UTF-8 bytes of <see cref="Payload"/>, of no bytes when there is none, in lowercase hex.</summary>
    public string PayloadDigest()
    {
        var text = Payload ?? string.Empty;
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(buffer.AsSpan(0, Encoding.UTF8.GetBytes(text, buffer)), hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The payload's <c>title</c> and <c>body</c> as the owner reads them, cut to <see cref="MaxListedTitleLength"/> and
    /// <see cref="MaxListedBodyLength"/> characters, each null when the payload holds no such text or does not parse, and
    /// whether either was cut.
    /// </summary>
    public (string? Title, string? Body, bool Clipped) ListedText() => TextOf(MaxListedTitleLength, MaxListedBodyLength);

    /// <summary>
    /// The payload's <c>title</c> and <c>body</c> in full, each null when the payload holds no such text or does not parse;
    /// once executed only <see cref="KeptAfterExecution"/> stays, which holds neither.
    /// </summary>
    public (string? Title, string? Body) FullText()
    {
        var (title, body, _) = TextOf(int.MaxValue, int.MaxValue);
        return (title, body);
    }

    private (string? Title, string? Body, bool Clipped) TextOf(int maxTitle, int maxBody)
    {
        if (string.IsNullOrWhiteSpace(Payload))
        {
            return (null, null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null, false);
            }

            var (title, titleCut) = Text(document.RootElement, "title", maxTitle);
            var (body, bodyCut) = Text(document.RootElement, "body", maxBody);
            return (title, body, titleCut || bodyCut);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    private static (string? Text, bool Cut) Text(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return (null, false);
        }

        var text = value.GetString()!;
        if (text.Length <= max)
        {
            return (text, false);
        }

        return (char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max], true);
    }
}
