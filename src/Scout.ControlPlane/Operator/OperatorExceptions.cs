namespace Scout.ControlPlane.Operator;

/// <summary>Thrown when an operator request carries a value outside what the operator accepts.</summary>
/// <remarks>Build the exception with what was wrong.</remarks>
public sealed class OperatorValidationException(string message) : Exception(message)
{
}

/// <summary>Thrown when a project or a claim does not exist.</summary>
/// <remarks>Build the exception naming what was not found.</remarks>
public sealed class OperatorNotFoundException(string message) : Exception(message)
{
}

/// <summary>Thrown when the worker saves operator state while the owner has not granted the feature's writes.</summary>
/// <remarks>Build the exception with the write gate's reason.</remarks>
public sealed class OperatorWriteRefusedException(string message) : Exception(message)
{
}

/// <summary>Thrown when the worker tries to change what only the owner or the desktop may set.</summary>
/// <remarks>Build the exception with what the worker may not change.</remarks>
public sealed class OperatorTamperingException(string message) : Exception(message)
{
}

/// <summary>Thrown when the operator's state cannot be written to Scout's data folder; the earlier state stays.</summary>
/// <remarks>Build the exception with what could not be saved and the I/O failure behind it.</remarks>
public sealed class OperatorStoreException(string message, Exception inner) : Exception(message, inner)
{
}
