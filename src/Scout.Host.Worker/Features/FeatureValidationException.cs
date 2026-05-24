namespace Scout.Host.Worker.Features;

/// <summary>
/// Thrown by an <see cref="IFeatureCredentialValidator"/> when the
/// credential body is structurally invalid or the secret was rejected.
/// The endpoint surface translates this to <c>400</c>.
/// </summary>
public sealed class FeatureValidationException : Exception
{
    /// <summary>Validator outcome that produced the failure.</summary>
    public FeatureCredentialValidationOutcome Outcome { get; }

    /// <summary>Build a validation exception.</summary>
    public FeatureValidationException(FeatureCredentialValidationOutcome outcome, string message)
        : base(message)
    {
        Outcome = outcome;
    }

    /// <summary>Build a validation exception with an inner cause.</summary>
    public FeatureValidationException(
        FeatureCredentialValidationOutcome outcome,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Outcome = outcome;
    }
}
