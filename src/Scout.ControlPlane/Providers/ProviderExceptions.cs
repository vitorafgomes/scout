namespace Scout.ControlPlane.Providers;

/// <summary>Thrown when the daemon never becomes reachable within the start window.</summary>
public sealed class ProviderStartTimeoutException : Exception
{
    public string ProviderId { get; }

    public ProviderStartTimeoutException(string id, string message)
        : base(message)
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when a lifecycle action targets a runtime not owned by the worker.</summary>
public sealed class ProviderNotManagedException : Exception
{
    public string ProviderId { get; }

    public ProviderNotManagedException(string id, string message)
        : base(message)
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when a lifecycle action targets a runtime that does not support it.</summary>
public sealed class ProviderLifecycleUnsupportedException : Exception
{
    public string ProviderId { get; }

    public ProviderLifecycleUnsupportedException(string id, string message)
        : base(message)
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when a provider id is not known.</summary>
public sealed class ProviderUnknownException : Exception
{
    public string ProviderId { get; }

    public ProviderUnknownException(string id)
        : base($"Unknown provider '{id}'.")
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when the spawned daemon exits before becoming reachable.</summary>
public sealed class ProviderStartFailedException : Exception
{
    public string ProviderId { get; }

    public ProviderStartFailedException(string id, string message)
        : base(message)
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when the daemon refuses to go away after a stop request.</summary>
public sealed class ProviderStopFailedException : Exception
{
    public string ProviderId { get; }

    public ProviderStopFailedException(string id, string message)
        : base(message)
    {
        ProviderId = id;
    }
}

/// <summary>Thrown when a lifecycle action is invoked before the readiness probe completes.</summary>
public sealed class ProviderNotReadyException : Exception
{
    public string ProviderId { get; }

    public ProviderNotReadyException(string providerId, string message)
        : base(message)
    {
        ProviderId = providerId;
    }
}
