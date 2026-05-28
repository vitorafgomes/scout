namespace Scout.Adapter.Teams;

/// <summary>
/// Settings for the Teams chat adapter. Bound from "Scout:Adapters:Teams".
/// Secrets (app password) must never be committed or logged — inject via
/// environment variables or User Secrets / Key Vault.
/// </summary>
public sealed class TeamsOptions
{
    /// <summary>
    /// Microsoft App (bot) id. Inject via SCOUT__ADAPTERS__TEAMS__APPID.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Microsoft App password / client secret. Never committed or logged —
    /// inject via SCOUT__ADAPTERS__TEAMS__APPPASSWORD or Key Vault. For a
    /// single-tenant or user-assigned managed-identity bot this may be empty.
    /// </summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>
    /// AAD tenant id for a single-tenant bot. Empty for multi-tenant.
    /// Inject via SCOUT__ADAPTERS__TEAMS__TENANTID.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}
