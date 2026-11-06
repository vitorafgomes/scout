namespace Scout.ControlPlane.Providers;

/// <summary>Thrown when the daemon never becomes reachable within the start window.</summary>
public sealed class ProviderStartTimeoutException(string id, string message) : Exception(message)
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when a lifecycle action targets a runtime not owned by the worker.</summary>
public sealed class ProviderNotManagedException(string id, string message) : Exception(message)
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when a lifecycle action targets a runtime that does not support it.</summary>
public sealed class ProviderLifecycleUnsupportedException(string id, string message) : Exception(message)
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when a provider id is not known.</summary>
public sealed class ProviderUnknownException(string id) : Exception($"Unknown provider '{id}'.")
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when the spawned daemon exits before becoming reachable.</summary>
public sealed class ProviderStartFailedException(string id, string message) : Exception(message)
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when the daemon refuses to go away after a stop request.</summary>
public sealed class ProviderStopFailedException(string id, string message) : Exception(message)
{
    public string ProviderId { get; } = id;
}

/// <summary>Thrown when a lifecycle action is invoked before the readiness probe completes.</summary>
public sealed class ProviderNotReadyException(string providerId, string message) : Exception(message)
{
    public string ProviderId { get; } = providerId;
}
