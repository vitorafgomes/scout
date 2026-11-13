namespace Scout.ControlPlane.Actions;

/// <summary>Thrown when a decision names an action the queue does not hold.</summary>
public sealed class PendingActionNotFoundException(string id) : Exception($"Unknown action '{id}'.")
{
    public string ActionId { get; } = id;
}

/// <summary>Thrown when an action that is not approved is marked as executed.</summary>
public sealed class PendingActionNotApprovedException(string id, PendingActionStatus status) : Exception($"Action '{id}' is {status.ToString().ToLowerInvariant()}, not approved.")
{
    public string ActionId { get; } = id;
}

/// <summary>Thrown when a decision targets an action the owner already decided.</summary>
public sealed class PendingActionAlreadyDecidedException(string id, PendingActionStatus status) : Exception($"Action '{id}' is already {status.ToString().ToLowerInvariant()}.")
{
    public string ActionId { get; } = id;
}
