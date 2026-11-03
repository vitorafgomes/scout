namespace Scout.ControlPlane.Operator;

/// <summary>The owner's brief for the channel: what it is for, who watches it and how it sounds.</summary>
public sealed record Mandate(
    string Objective,
    string Audience,
    IReadOnlyList<string> Pillars,
    string Cadence,
    string Language,
    string VoiceAndStyle,
    IReadOnlyList<string> BlockedTopics,
    string TargetMetric,
    string TargetValue,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>The mandate before the owner wrote one.</summary>
    public static Mandate Empty { get; } = new(
        string.Empty, string.Empty, [], string.Empty, string.Empty, string.Empty, [], string.Empty, string.Empty);
}

/// <summary>
/// One video the operator works on. <c>OriginReference</c> is the opaque reference to the chat
/// conversation that asked for it; only the worker ever receives it.
/// </summary>
public sealed record OperatorProject(
    string Id,
    string Topic,
    string Status,
    string Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TopicCandidate> Candidates,
    Script? Script,
    IReadOnlyList<Claim> Claims,
    string? Error = null,
    string? OriginReference = null);

/// <summary>A topic the research stage proposes, with why it fits the mandate.</summary>
public sealed record TopicCandidate(string Title, string Reason, double Fit);

/// <summary>The script draft and its package metadata.</summary>
public sealed record Script(
    string Hook,
    IReadOnlyList<ScriptSection> Sections,
    string CallToAction,
    IReadOnlyList<string> TitleVariants,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Chapter> Chapters);

/// <summary>One section of the script.</summary>
public sealed record ScriptSection(string Heading, string Narration);

/// <summary>One chapter marker of the video description.</summary>
public sealed record Chapter(string At, string Title);

/// <summary>
/// A factual claim of the script. <c>DecidedAt</c> is set only when the owner decided the claim;
/// the worker can never change or drop such a claim.
/// </summary>
public sealed record Claim(
    string Id,
    string Text,
    string Risk,
    IReadOnlyList<string> SourceUrls,
    string Status,
    string? Note = null,
    DateTimeOffset? DecidedAt = null);
