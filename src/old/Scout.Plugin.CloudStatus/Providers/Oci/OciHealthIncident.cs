namespace Scout.Plugin.CloudStatus.Providers.Oci;

/// <summary>
/// Minimum projection of an Oracle Cloud status incident. The provider maps
/// these into <see cref="Domain.ServiceStatus"/> entries. Decoupled from the
/// raw JSON DTOs in <c>OciHealthSource</c> so fakes stay ergonomic.
/// </summary>
/// <param name="Id">Incident identifier — stable across polls.</param>
/// <param name="Service">Service name, e.g. "Compute", "Object Storage".</param>
/// <param name="Region">Region slug, e.g. "eu-london-1", or "(global)".</param>
/// <param name="Impact">
/// Severity string reported by Oracle. Expected values: <c>critical</c>,
/// <c>major</c>, <c>minor</c>, <c>none</c>. Unknown values are treated as
/// <see cref="Domain.HealthLevel.Degraded"/>.
/// </param>
/// <param name="Title">Short human-readable title.</param>
public sealed record OciHealthIncident(
    string Id,
    string Service,
    string Region,
    string Impact,
    string Title);
