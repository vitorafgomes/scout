namespace Scout.Plugin.CloudStatus.Providers.Oci;

/// <summary>
/// Seam over the Oracle Cloud status endpoint. Wraps the HTTP call so
/// <see cref="OciCloudHealthProvider"/> can be unit-tested without a live
/// network, and so tests for the default <see cref="OciHealthSource"/> can
/// use an <see cref="HttpMessageHandler"/> fake.
/// </summary>
public interface IOciHealthSource
{
    /// <summary>
    /// Return currently-active OCI incidents for the configured filter.
    /// An empty list means Oracle Cloud reports everything green right now.
    /// Implementations propagate <see cref="OperationCanceledException"/>.
    /// Other exceptions bubble so the provider can map them.
    /// </summary>
    Task<IReadOnlyList<OciHealthIncident>> GetActiveIncidentsAsync(CancellationToken cancellationToken);
}
