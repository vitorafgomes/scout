using System.Text.Json;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;

namespace Scout.Host.Console;

/// <summary>Structured error payload for non-2xx control-plane responses.</summary>
internal sealed record ErrorDto(string Code, string Message);

internal sealed record ProviderErrorDto(string Code, string Message);

internal sealed record ProviderModelDto(string Name, long SizeBytes, string ModifiedAt);

internal sealed record ProviderStatusDto(
    string Id,
    bool Installed,
    bool Running,
    string? Endpoint,
    string? Version,
    bool ManagedByWorker,
    bool Autostart,
    bool SupportsLifecycle,
    ProviderErrorDto? LastError,
    IReadOnlyList<ProviderModelDto> Models);

internal sealed record FeatureCredentialFieldDto(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel);

internal sealed record FeatureSettingFieldDto(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel,
    bool Required);

internal sealed record FeatureScopeRowDto(string Scope, IReadOnlyList<string> Actions);

internal sealed record FeatureDto(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    bool HasCredentials,
    bool Enabled,
    string Status,
    string? LastError,
    IReadOnlyList<FeatureCredentialFieldDto> CredentialFields,
    IReadOnlyList<FeatureSettingFieldDto> SettingsFields,
    IReadOnlyDictionary<string, string?> Settings,
    IReadOnlyList<FeatureScopeRowDto>? ScopeMatrix,
    IReadOnlyList<string>? SetupSteps);

/// <summary>Body of a <c>PUT /providers/{id}/autostart</c> request.</summary>
internal sealed record AutostartRequestDto(bool Enabled);

/// <summary>Body of a <c>POST /features/{id}/setup</c> request.</summary>
internal sealed record SetupRequestDto(
    Dictionary<string, string>? Credentials,
    Dictionary<string, string?>? Settings);

/// <summary>
/// Maps the transport-agnostic domain snapshots onto the JSON DTOs the desktop
/// UI consumes. The DTO field names mirror the shared IPC contract; JSON is
/// serialised with the camelCase web defaults.
/// </summary>
internal static class ControlPlaneHttpContracts
{
    public static ProviderStatusDto ToDto(ProviderStatus status) => new(
        Id: status.Id,
        Installed: status.Installed,
        Running: status.Running,
        Endpoint: status.Endpoint,
        Version: status.Version,
        ManagedByWorker: status.ManagedByWorker,
        Autostart: status.Autostart,
        SupportsLifecycle: status.SupportsLifecycle,
        LastError: status.LastError is null
            ? null
            : new ProviderErrorDto(status.LastError.Code, status.LastError.Message),
        Models: status.Models
            .Select(m => new ProviderModelDto(m.Name, m.SizeBytes, m.ModifiedAt))
            .ToArray());

    public static FeatureDto ToDto(FeatureSnapshot snapshot) => new(
        Id: snapshot.Id,
        DisplayName: snapshot.DisplayName,
        Description: snapshot.Description,
        RequiresCredentials: snapshot.RequiresCredentials,
        HasCredentials: snapshot.HasCredentials,
        Enabled: snapshot.Enabled,
        Status: ToWireStatus(snapshot.Status),
        LastError: snapshot.LastError,
        CredentialFields: snapshot.CredentialFields
            .Select(f => new FeatureCredentialFieldDto(
                f.Key, f.Kind, f.Label, f.HelperText, f.CreateLinkUrl, f.CreateLinkLabel))
            .ToArray(),
        SettingsFields: snapshot.SettingsFields
            .Select(f => new FeatureSettingFieldDto(
                f.Key, f.Kind, f.Label, f.HelperText, f.CreateLinkUrl, f.CreateLinkLabel, f.Required))
            .ToArray(),
        Settings: snapshot.Settings,
        ScopeMatrix: snapshot.ScopeMatrix?
            .Select(r => new FeatureScopeRowDto(r.Scope, r.Actions))
            .ToArray(),
        SetupSteps: snapshot.SetupSteps);

    private static string ToWireStatus(FeatureStatusKind kind) => kind switch
    {
        FeatureStatusKind.Enabled => "enabled",
        FeatureStatusKind.NeedsCredentials => "needs-credentials",
        FeatureStatusKind.Disabled => "disabled",
        FeatureStatusKind.Error => "error",
        _ => "disabled",
    };
}

/// <summary>
/// Maps domain exceptions onto the HTTP status and the stable error code the
/// desktop UI already knows how to render. Codes mirror the shared error
/// contract so the existing renderer mapping keeps working unchanged.
/// </summary>
internal static class ControlPlaneErrorMapper
{
    public static (int Status, string Code, string Message) Map(Exception exception) => exception switch
    {
        ProviderUnknownException e => (404, "providers/unknown", e.Message),
        ProviderLifecycleUnsupportedException e => (501, "providers/lifecycle-unsupported", e.Message),
        ProviderNotManagedException e => (412, "providers/not-managed", e.Message),
        ProviderNotReadyException e => (503, "providers/unreachable", e.Message),
        ProviderStartTimeoutException e => (504, "providers/start-timeout", e.Message),
        ProviderStartFailedException e => (500, "providers/request-failed", e.Message),
        ProviderStopFailedException e => (500, "providers/request-failed", e.Message),
        HttpRequestException e => (503, "providers/unreachable", e.Message),
        FeatureUnknownException e => (404, "features/unknown", e.Message),
        FeatureValidationFailedException e => (400, "features/validation-failed", DescribeValidation(e)),
        FeatureCredentialsMissingException e => (412, "features/credentials-missing", e.Message),
        FeatureNotImplementedException e => (501, "features/not-implemented", e.Message),
        FeatureActivationException e => (500, "features/activation-failed", e.Message),
        FeaturePersistFailedException e => (500, "features/persist-failed", e.Message),
        JsonException => (400, "providers/request-failed", "Invalid request body."),
        ArgumentException e => (400, "providers/request-failed", e.Message),
        _ => (500, "providers/request-failed", exception.Message),
    };

    private static string DescribeValidation(FeatureValidationFailedException exception) =>
        string.Join("; ", exception.Errors.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
}
