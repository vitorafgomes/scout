namespace Scout.Host.Worker.Features;

/// <summary>
/// Wire shape returned by <c>/features</c>. Mirrors the Angular
/// <c>FeatureDto</c> contract in <c>ui/packages/shared</c>.
/// </summary>
public sealed record FeatureDto(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    bool HasCredentials,
    bool Enabled,
    string Status,
    string? LastError);
