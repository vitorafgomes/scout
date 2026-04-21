namespace Scout.Plugin.CloudStatus.Providers.Azure;

/// <summary>
/// Minimum projection of an Azure Service Health event that the provider
/// needs to build a <see cref="Domain.StatusReport"/>. Decoupled from any
/// Azure SDK type so the adapter can be faked in unit tests without pulling
/// <c>Azure.Core.TestFramework</c>.
/// </summary>
/// <param name="Id">Tracking id — unique per event.</param>
/// <param name="Title">Short human-readable title.</param>
/// <param name="Level">
/// Severity reported by Azure Service Health. Expected values include
/// <c>Critical</c>, <c>Error</c>, <c>Warning</c>, <c>Informational</c>,
/// <c>Healthy</c>. Unknown values are treated as <c>Informational</c>.
/// </param>
/// <param name="ImpactedService">Service name, e.g. "Azure SQL Database".</param>
/// <param name="Region">Affected Azure region, e.g. "westeurope".</param>
public sealed record AzureHealthEvent(
    string Id,
    string Title,
    string Level,
    string ImpactedService,
    string Region);
