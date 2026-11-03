namespace Scout.ControlPlane.Operator;

/// <summary>Thrown when an operator request carries a value outside what the operator accepts.</summary>
public sealed class OperatorValidationException : Exception
{
    /// <summary>Build the exception with what was wrong.</summary>
    public OperatorValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when a project or a claim does not exist.</summary>
public sealed class OperatorNotFoundException : Exception
{
    /// <summary>Build the exception naming what was not found.</summary>
    public OperatorNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the worker saves operator state while the owner has not granted the feature's writes.</summary>
public sealed class OperatorWriteRefusedException : Exception
{
    /// <summary>Build the exception with the write gate's reason.</summary>
    public OperatorWriteRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the worker tries to change what only the owner or the desktop may set.</summary>
public sealed class OperatorTamperingException : Exception
{
    /// <summary>Build the exception with what the worker may not change.</summary>
    public OperatorTamperingException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the operator's state cannot be written to Scout's data folder; the earlier state stays.</summary>
public sealed class OperatorStoreException : Exception
{
    /// <summary>Build the exception with what could not be saved and the I/O failure behind it.</summary>
    public OperatorStoreException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
