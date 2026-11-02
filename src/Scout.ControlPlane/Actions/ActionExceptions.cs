namespace Scout.ControlPlane.Actions;

/// <summary>Thrown when a decision names an action the queue does not hold.</summary>
public sealed class PendingActionNotFoundException : Exception
{
    public string ActionId { get; }

    public PendingActionNotFoundException(string id)
        : base($"Unknown action '{id}'.")
    {
        ActionId = id;
    }
}

/// <summary>Thrown when an action that is not approved is marked as executed.</summary>
public sealed class PendingActionNotApprovedException : Exception
{
    public string ActionId { get; }

    public PendingActionNotApprovedException(string id, PendingActionStatus status)
        : base($"Action '{id}' is {status.ToString().ToLowerInvariant()}, not approved.")
    {
        ActionId = id;
    }
}

/// <summary>Thrown when a decision targets an action the owner already decided.</summary>
public sealed class PendingActionAlreadyDecidedException : Exception
{
    public string ActionId { get; }

    public PendingActionAlreadyDecidedException(string id, PendingActionStatus status)
        : base($"Action '{id}' is already {status.ToString().ToLowerInvariant()}.")
    {
        ActionId = id;
    }
}
