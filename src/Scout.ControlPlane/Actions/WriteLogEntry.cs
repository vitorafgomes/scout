namespace Scout.ControlPlane.Actions;

/// <summary>
/// One write a feature performed: what, where, when, how it ended, and who
/// approved it (<c>"owner"</c>, or null for a reversible write under the grant).
/// </summary>
public sealed record WriteLogEntry(
    DateTimeOffset At,
    string FeatureId,
    string Action,
    string Target,
    string Outcome,
    string? ApprovedBy,
    string? ActionId);
