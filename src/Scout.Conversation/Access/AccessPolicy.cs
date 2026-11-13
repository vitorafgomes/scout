namespace Scout.Conversation.Access;

/// <summary>
/// Who may use Scout, decided once from <see cref="AccessOptions"/> for every
/// caller: with a non-empty list only its members (compared ordinally after
/// trimming), with an empty list nobody when <see cref="AccessOptions.RequireAllowList"/>
/// is set and everybody otherwise.
/// </summary>
public sealed class AccessPolicy
{
    private readonly string[] _allowed;
    private readonly bool _requireAllowList;
    private readonly string[] _owners;

    /// <summary>Builds the policy from the bound options.</summary>
    public AccessPolicy(AccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _allowed = options.AllowedUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();
        _requireAllowList = options.RequireAllowList;
        _owners = options.OwnerUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();
        PairingCode = options.PairingCode;
        PairingExpiresAt = options.PairingExpiresAt;
    }

    /// <summary>The one-time code the owner may send in a direct message to be added; it never changes <see cref="IsAllowed"/>.</summary>
    public string? PairingCode { get; }

    /// <summary>When <see cref="PairingCode"/> stops being accepted.</summary>
    public DateTimeOffset? PairingExpiresAt { get; }

    /// <summary>True when the owner listed at least one user id.</summary>
    public bool HasAllowList => _allowed.Length > 0;

    /// <summary>True when <paramref name="userId"/> may use Scout.</summary>
    public bool IsAllowed(string userId)
        => HasAllowList
            ? _allowed.Contains(userId.Trim(), StringComparer.Ordinal)
            : !_requireAllowList;

    /// <summary>True when <paramref name="userId"/> may use Scout and is listed in <see cref="AccessOptions.OwnerUserIds"/>.</summary>
    public bool IsOwner(string userId)
        => IsAllowed(userId) && _owners.Contains(userId.Trim(), StringComparer.Ordinal);
}
