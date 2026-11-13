namespace Scout.ControlPlane.Actions;

/// <summary>
/// One write a feature performed, or one decision on an action: what, where, when, how it ended, who
/// approved it (<c>"owner"</c>, or null for a reversible write under the grant), the identity that signed
/// the write on the external system, when the worker knows it, and for the owner's control on a running job its status,
/// one of the words of <see cref="Controls.OwnerControlStatuses"/>; null on every other entry.
/// </summary>
public sealed record WriteLogEntry(
    DateTimeOffset At,
    string FeatureId,
    string Action,
    string Target,
    string Outcome,
    string? ApprovedBy,
    string? ActionId,
    string? SignedAs = null,
    string? ControlStatus = null)
{
    /// <summary>The entry that records the rejection of <paramref name="action"/> with <paramref name="reason"/>, as the owner's.</summary>
    public static WriteLogEntry Rejected(PendingAction action, string? reason)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new(
            DateTimeOffset.UtcNow,
            action.FeatureId,
            action.Action,
            action.Target,
            string.IsNullOrWhiteSpace(reason) ? "rejected" : $"rejected: {reason}",
            "owner",
            action.Id)
        {
            GroupId = action.GroupId,
        };
    }

    /// <summary>The <see cref="PendingAction.GroupId"/> of the action the entry is about; null when it has none.</summary>
    public string? GroupId { get; init; }
}
