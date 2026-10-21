namespace Scout.ControlPlane.Features;

/// <summary>
/// Domain facade for the Features subsystem. Ports the operation logic from
/// the old <c>FeaturesEndpoints</c> with the HTTP shell stripped: validation,
/// credential persistence, settings merge, enable/disable activation and the
/// merged-snapshot build. The gRPC service is a thin adapter over this type.
/// </summary>
public sealed class FeaturesService
{
    private const string CredentialStoreUnavailableMessage =
        "Credential store is unavailable; the credential was NOT saved. "
        + "Secrets are held in process memory only and no durable at-rest "
        + "backend is reachable on this host.";

    private readonly IFeatureCatalog _catalog;
    private readonly IFeatureStateStore _stateStore;
    private readonly ICredentialStore _credentials;
    private readonly IFeatureActivator _activator;
    private readonly IReadOnlyList<IFeatureCredentialValidator> _validators;

    public FeaturesService(
        IFeatureCatalog catalog,
        IFeatureStateStore stateStore,
        ICredentialStore credentials,
        IFeatureActivator activator,
        IEnumerable<IFeatureCredentialValidator> validators)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _validators = (validators ?? throw new ArgumentNullException(nameof(validators))).ToArray();
    }

    /// <summary>Return the merged snapshot for every catalog feature.</summary>
    public async Task<IReadOnlyList<FeatureSnapshot>> ListAsync(CancellationToken ct)
    {
        var persisted = await _stateStore.GetAllAsync(ct).ConfigureAwait(false);
        var result = new List<FeatureSnapshot>(_catalog.All.Count);
        foreach (var metadata in _catalog.All)
        {
            result.Add(await BuildSnapshotAsync(metadata, persisted, ct).ConfigureAwait(false));
        }
        return result;
    }

    /// <summary>Validate and persist credentials for a feature.</summary>
    public async Task<FeatureSnapshot> SetCredentialsAsync(
        string id,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct)
    {
        var metadata = Require(id);
        ArgumentNullException.ThrowIfNull(body);

        var validator = FindValidator(id);
        if (validator is not null)
        {
            FeatureCredentialValidationResult result;
            try
            {
                result = await validator.ValidateAsync(body, ct).ConfigureAwait(false);
            }
            catch (FeatureValidationException ex)
            {
                throw new FeatureValidationFailedException(
                    SingleError("credentials", ex.Message));
            }

            if (result.Outcome != FeatureCredentialValidationOutcome.Valid)
            {
                throw new FeatureValidationFailedException(
                    SingleError("credentials", result.Detail ?? $"Validation outcome: {result.Outcome}."));
            }
        }

        // Fail closed on each write: if a secret would be persisted but the
        // store has no durable backend, tell the caller it was NOT saved rather
        // than reporting success. Required-field validation runs first so its
        // more specific error still takes precedence.
        foreach (var field in metadata.CredentialFields)
        {
            var required = IsRequired(field);
            if (!body.TryGetValue(field.Key, out var value)
                || (required && string.IsNullOrWhiteSpace(value)))
            {
                if (required)
                {
                    throw new FeatureValidationFailedException(
                        SingleError($"credentials.{field.Key}", $"Missing required field '{field.Key}'."));
                }
                continue;
            }

            if (!_credentials.IsAvailable)
            {
                throw new FeaturePersistFailedException(CredentialStoreUnavailableMessage);
            }

            var stored = await _credentials.StoreAsync(
                CredentialKey(metadata.Id, field.Key),
                value,
                ct).ConfigureAwait(false);
            if (!stored)
            {
                throw new FeaturePersistFailedException(CredentialStoreUnavailableMessage);
            }
        }

        return await BuildSnapshotAsync(metadata, ct).ConfigureAwait(false);
    }

    /// <summary>Validate and persist credentials and settings for a feature.</summary>
    public async Task<FeatureSnapshot> SetupAsync(
        string id,
        FeatureSetupInput input,
        CancellationToken ct)
    {
        var metadata = Require(id);
        ArgumentNullException.ThrowIfNull(input);

        var suppliedCredentials = input.Credentials
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var suppliedSettings = input.Settings
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);

        // Validate the whole request before any persistence so a bad body
        // never leaves a half-written feature behind.
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in metadata.CredentialFields.Where(IsRequired))
        {
            if (!suppliedCredentials.TryGetValue(field.Key, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                errors[$"credentials.{field.Key}"] = $"Missing required credential '{field.Key}'.";
            }
        }

        foreach (var field in metadata.SettingsFields.Where(f => f.Required))
        {
            if (!suppliedSettings.TryGetValue(field.Key, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                errors[$"settings.{field.Key}"] = $"Missing required setting '{field.Key}'.";
            }
        }

        var validator = FindValidator(id);
        if (validator is not null && suppliedCredentials.Count > 0)
        {
            FeatureCredentialValidationResult result;
            try
            {
                result = await validator.ValidateAsync(suppliedCredentials, ct).ConfigureAwait(false);
            }
            catch (FeatureValidationException ex)
            {
                result = new FeatureCredentialValidationResult(
                    FeatureCredentialValidationOutcome.Invalid,
                    ex.Message);
            }

            if (result.Outcome != FeatureCredentialValidationOutcome.Valid)
            {
                var detail = result.Detail ?? $"Validation outcome: {result.Outcome}.";
                var key = metadata.CredentialFields.Count > 0
                    ? $"credentials.{metadata.CredentialFields[0].Key}"
                    : "credentials";
                errors[key] = detail;
            }
        }

        if (errors.Count > 0)
        {
            throw new FeatureValidationFailedException(errors);
        }

        // Fail closed before touching the store: if any credential would be
        // written but no durable backend is reachable, reject up front so no
        // settings are half-applied and the caller learns the secret was not
        // saved.
        var willPersistCredential = metadata.CredentialFields.Any(field =>
            suppliedCredentials.TryGetValue(field.Key, out var value)
            && !string.IsNullOrWhiteSpace(value));

        if (willPersistCredential && !_credentials.IsAvailable)
        {
            throw new FeaturePersistFailedException(CredentialStoreUnavailableMessage);
        }

        // Persist credentials first, then settings. Both are idempotent per
        // key, so a settings failure after a credential write leaves a
        // re-runnable state, never a partially-applied request.
        try
        {
            foreach (var field in metadata.CredentialFields)
            {
                if (!suppliedCredentials.TryGetValue(field.Key, out var value)
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var stored = await _credentials.StoreAsync(
                    CredentialKey(metadata.Id, field.Key),
                    value,
                    ct).ConfigureAwait(false);
                if (!stored)
                {
                    throw new FeaturePersistFailedException(CredentialStoreUnavailableMessage);
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or FeaturePersistFailedException))
        {
            throw new FeaturePersistFailedException($"Failed to persist credentials: {ex.Message}");
        }

        if (suppliedSettings.Count > 0)
        {
            try
            {
                var state = await _stateStore.GetAsync(id, ct).ConfigureAwait(false);
                var merged = FeatureStates.EmptySettings();
                foreach (var kvp in state.Settings)
                {
                    merged[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in suppliedSettings)
                {
                    if (kvp.Value is null)
                    {
                        merged.Remove(kvp.Key);
                    }
                    else
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                }

                await _stateStore.SetAsync(id, state with { Settings = merged }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FeaturePersistFailedException($"Failed to persist settings: {ex.Message}");
            }
        }

        return await BuildSnapshotAsync(metadata, ct).ConfigureAwait(false);
    }

    /// <summary>Remove credentials for a feature and disable it.</summary>
    public async Task<FeatureSnapshot> DeleteCredentialsAsync(string id, CancellationToken ct)
    {
        var metadata = Require(id);

        foreach (var field in metadata.CredentialFields)
        {
            await _credentials.DeleteAsync(CredentialKey(metadata.Id, field.Key), ct).ConfigureAwait(false);
        }

        if (_activator.IsActive(id))
        {
            await _activator.DeactivateAsync(id, ct).ConfigureAwait(false);
        }

        await _stateStore.SetAsync(id, FeatureStates.EmptyDisabled(), ct).ConfigureAwait(false);
        return await BuildSnapshotAsync(metadata, ct).ConfigureAwait(false);
    }

    /// <summary>Enable a feature.</summary>
    public async Task<FeatureSnapshot> EnableAsync(string id, CancellationToken ct)
    {
        var metadata = Require(id);

        if (metadata.RequiresCredentials)
        {
            foreach (var field in metadata.CredentialFields.Where(IsRequired))
            {
                if (!await _credentials.ContainsAsync(CredentialKey(metadata.Id, field.Key), ct).ConfigureAwait(false))
                {
                    throw new FeatureCredentialsMissingException(id);
                }
            }
        }

        if (!_activator.CanActivate(id))
        {
            throw new FeatureNotImplementedException(id);
        }

        try
        {
            await _activator.ActivateAsync(id, ct).ConfigureAwait(false);
            var existing = await _stateStore.GetAsync(id, ct).ConfigureAwait(false);
            await _stateStore.SetAsync(
                    id,
                    existing with { Enabled = true, LastError = null },
                    ct)
                .ConfigureAwait(false);
        }
        catch (FeatureActivationException ex)
        {
            var existing = await _stateStore.GetAsync(id, ct).ConfigureAwait(false);
            await _stateStore.SetAsync(
                    id,
                    existing with { Enabled = false, LastError = ex.Message },
                    ct)
                .ConfigureAwait(false);
            throw;
        }

        return await BuildSnapshotAsync(metadata, ct).ConfigureAwait(false);
    }

    /// <summary>Disable a feature.</summary>
    public async Task<FeatureSnapshot> DisableAsync(string id, CancellationToken ct)
    {
        var metadata = Require(id);

        if (_activator.IsActive(id))
        {
            await _activator.DeactivateAsync(id, ct).ConfigureAwait(false);
        }

        await _stateStore.SetAsync(id, FeatureStates.EmptyDisabled(), ct).ConfigureAwait(false);
        return await BuildSnapshotAsync(metadata, ct).ConfigureAwait(false);
    }

    private FeatureMetadata Require(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _catalog.Find(id) ?? throw new FeatureUnknownException(id);
    }

    private IFeatureCredentialValidator? FindValidator(string id) =>
        _validators.FirstOrDefault(v => string.Equals(v.FeatureId, id, StringComparison.Ordinal));

    private async Task<FeatureSnapshot> BuildSnapshotAsync(FeatureMetadata metadata, CancellationToken ct)
    {
        var persisted = await _stateStore.GetAllAsync(ct).ConfigureAwait(false);
        return await BuildSnapshotAsync(metadata, persisted, ct).ConfigureAwait(false);
    }

    private async Task<FeatureSnapshot> BuildSnapshotAsync(
        FeatureMetadata metadata,
        IReadOnlyDictionary<string, FeatureState> persisted,
        CancellationToken ct)
    {
        var state = persisted.TryGetValue(metadata.Id, out var s)
            ? s
            : FeatureStates.EmptyDisabled();

        var hasCredentials = true;
        if (metadata.RequiresCredentials)
        {
            foreach (var field in metadata.CredentialFields.Where(IsRequired))
            {
                if (!await _credentials.ContainsAsync(CredentialKey(metadata.Id, field.Key), ct).ConfigureAwait(false))
                {
                    hasCredentials = false;
                    break;
                }
            }
        }

        var status = ResolveStatus(metadata, state, hasCredentials);

        return new FeatureSnapshot(
            Id: metadata.Id,
            DisplayName: metadata.DisplayName,
            Description: metadata.Description,
            RequiresCredentials: metadata.RequiresCredentials,
            HasCredentials: hasCredentials,
            Enabled: state.Enabled,
            Status: status,
            LastError: state.LastError,
            CredentialFields: metadata.CredentialFields,
            SettingsFields: metadata.SettingsFields,
            Settings: state.Settings,
            ScopeMatrix: metadata.ScopeMatrix,
            SetupSteps: metadata.SetupSteps);
    }

    // Plain "text" inputs are optional metadata; any other kind ("password",
    // "textarea") is required for the feature to be considered ready.
    private static bool IsRequired(FeatureCredentialField field) =>
        !string.Equals(field.Kind, "text", StringComparison.Ordinal);

    private FeatureStatusKind ResolveStatus(
        FeatureMetadata metadata,
        FeatureState state,
        bool hasCredentials)
    {
        if (state.LastError is not null)
        {
            return FeatureStatusKind.Error;
        }

        if (state.Enabled && _activator.CanActivate(metadata.Id))
        {
            return FeatureStatusKind.Enabled;
        }

        if (metadata.RequiresCredentials && !hasCredentials)
        {
            return FeatureStatusKind.NeedsCredentials;
        }

        return FeatureStatusKind.Disabled;
    }

    private static string CredentialKey(string featureId, string fieldKey) =>
        $"scout/{featureId}/{fieldKey}";

    private static Dictionary<string, string> SingleError(string key, string message) =>
        new(StringComparer.Ordinal) { [key] = message };
}
