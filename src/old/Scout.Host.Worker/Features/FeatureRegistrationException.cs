namespace Scout.Host.Worker.Features;

/// <summary>
/// Thrown by <see cref="IFeatureKernelRegistrar"/> when a plugin factory
/// fails to build the kernel plugin. The endpoint surface translates
/// this to <c>500 features/registrar-failed</c>.
/// </summary>
public sealed class FeatureRegistrationException : Exception
{
    /// <summary>Feature id that failed to register.</summary>
    public string FeatureId { get; }

    /// <summary>Build a registration exception.</summary>
    public FeatureRegistrationException(string featureId, string message, Exception innerException)
        : base(message, innerException)
    {
        FeatureId = featureId;
    }
}
