namespace Scout.Host.Worker.Features;

/// <summary>
/// One non-secret configuration input the UI must render in the feature
/// setup dialog. Values typed into these fields persist in the feature
/// state store (in clear) and travel back to clients on every
/// <c>GET /features</c> response via <see cref="FeatureDto.Settings"/>.
/// </summary>
/// <param name="Key">Stable identifier used as the dictionary key in the
/// <c>POST /features/{id}/setup</c> body and in the persisted state.</param>
/// <param name="Kind">Widget hint mirroring the credential vocabulary:
/// <c>text</c>, <c>textarea</c> or <c>password</c>. Password is allowed
/// even though settings are not secrets, in case a future setting wants
/// to mask the input visually.</param>
/// <param name="Label">Human-readable label shown above the input.</param>
/// <param name="HelperText">Optional secondary text rendered beneath
/// the input.</param>
/// <param name="CreateLinkUrl">Optional deep link to a provider page
/// where the value can be looked up or created.</param>
/// <param name="CreateLinkLabel">Display text for
/// <paramref name="CreateLinkUrl"/>.</param>
/// <param name="Required">When <see langword="true"/>, the setup
/// endpoint rejects a request that omits the key or leaves it blank.</param>
public sealed record FeatureSettingFieldDto(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel,
    bool Required);
