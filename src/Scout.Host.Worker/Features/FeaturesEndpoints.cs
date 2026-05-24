using Scout.Core.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Scout.Host.Worker.Features;

/// <summary>
/// HTTP surface for the Features subsystem. Mirrors the structure of
/// <c>ProvidersEndpoints</c>.
/// </summary>
public static class FeaturesEndpoints
{
    /// <summary>Register the <c>/features</c> route group on the app.</summary>
    public static IEndpointRouteBuilder MapFeaturesApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/features").WithTags("Features");

        group.MapGet("/", async (
                IFeatureCatalog catalog,
                IFeatureStateStore stateStore,
                ICredentialStore credentials,
                FeaturePluginFactoryRegistry factories,
                CancellationToken ct) =>
            {
                var persisted = await stateStore.GetAllAsync(ct).ConfigureAwait(false);
                var dtos = new List<FeatureDto>(catalog.All.Count);
                foreach (var metadata in catalog.All)
                {
                    var dto = await BuildDtoAsync(metadata, persisted, credentials, factories, ct)
                        .ConfigureAwait(false);
                    dtos.Add(dto);
                }
                return Results.Ok(dtos);
            })
            .WithName("ListFeatures")
            .WithSummary("List every Scout feature")
            .WithDescription("Returns the merged catalog + persisted state + credential presence snapshot for each feature.")
            .Produces<FeatureDto[]>(StatusCodes.Status200OK);

        group.MapPost("/{id}/credentials", async (
                string id,
                [FromBody] Dictionary<string, string> body,
                IFeatureCatalog catalog,
                IFeatureStateStore stateStore,
                ICredentialStore credentials,
                IEnumerable<IFeatureCredentialValidator> validators,
                FeaturePluginFactoryRegistry factories,
                CancellationToken ct) =>
            {
                var metadata = catalog.Find(id);
                if (metadata is null)
                {
                    return Unknown(id);
                }

                ArgumentNullException.ThrowIfNull(body);

                var validator = validators.FirstOrDefault(v =>
                    string.Equals(v.FeatureId, id, StringComparison.Ordinal));
                if (validator is not null)
                {
                    FeatureCredentialValidationResult result;
                    try
                    {
                        result = await validator.ValidateAsync(body, ct).ConfigureAwait(false);
                    }
                    catch (FeatureValidationException ex)
                    {
                        return InvalidCredentialBody(ex.Message);
                    }

                    if (result.Outcome != FeatureCredentialValidationOutcome.Valid)
                    {
                        return InvalidCredentialBody(
                            result.Detail ?? $"Validation outcome: {result.Outcome}.");
                    }
                }

                foreach (var field in metadata.CredentialFields)
                {
                    var required = IsRequired(field);
                    if (!body.TryGetValue(field.Key, out var value)
                        || (required && string.IsNullOrWhiteSpace(value)))
                    {
                        if (required)
                        {
                            return InvalidCredentialBody(
                                $"Missing required field '{field.Key}'.");
                        }
                        continue;
                    }

                    await credentials.StoreAsync(
                        $"scout/{metadata.Id}/{field.Key}",
                        value,
                        ct).ConfigureAwait(false);
                }

                var snapshot = await stateStore.GetAllAsync(ct).ConfigureAwait(false);
                var dto = await BuildDtoAsync(metadata, snapshot, credentials, factories, ct)
                    .ConfigureAwait(false);
                return Results.Ok(dto);
            })
            .WithName("SetFeatureCredentials")
            .WithSummary("Validate and persist credentials for a feature")
            .Produces<FeatureDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/credentials", async (
                string id,
                IFeatureCatalog catalog,
                IFeatureStateStore stateStore,
                ICredentialStore credentials,
                IFeatureKernelRegistrar registrar,
                FeaturePluginFactoryRegistry factories,
                CancellationToken ct) =>
            {
                var metadata = catalog.Find(id);
                if (metadata is null)
                {
                    return Unknown(id);
                }

                foreach (var field in metadata.CredentialFields)
                {
                    await credentials.DeleteAsync(
                        $"scout/{metadata.Id}/{field.Key}",
                        ct).ConfigureAwait(false);
                }

                // Disabling is the only safe outcome once credentials are
                // wiped; otherwise the feature would stay attached without
                // a backing secret.
                if (registrar.IsRegistered(id))
                {
                    await registrar.UnregisterAsync(id, ct).ConfigureAwait(false);
                }

                await stateStore.SetAsync(id, new FeatureState(false, null), ct)
                    .ConfigureAwait(false);

                var snapshot = await stateStore.GetAllAsync(ct).ConfigureAwait(false);
                var dto = await BuildDtoAsync(metadata, snapshot, credentials, factories, ct)
                    .ConfigureAwait(false);
                return Results.Ok(dto);
            })
            .WithName("DeleteFeatureCredentials")
            .WithSummary("Remove credentials for a feature and disable it")
            .Produces<FeatureDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/enable", async (
                string id,
                IFeatureCatalog catalog,
                IFeatureStateStore stateStore,
                ICredentialStore credentials,
                IFeatureKernelRegistrar registrar,
                FeaturePluginFactoryRegistry factories,
                CancellationToken ct) =>
            {
                var metadata = catalog.Find(id);
                if (metadata is null)
                {
                    return Unknown(id);
                }

                if (metadata.RequiresCredentials)
                {
                    foreach (var field in metadata.CredentialFields.Where(IsRequired))
                    {
                        var key = $"scout/{metadata.Id}/{field.Key}";
                        if (!await credentials.ContainsAsync(key, ct).ConfigureAwait(false))
                        {
                            return CredentialsMissing(id);
                        }
                    }
                }

                if (!factories.HasFactory(id))
                {
                    return NotYetImplemented(id);
                }

                try
                {
                    await registrar.RegisterAsync(id, ct).ConfigureAwait(false);
                    await stateStore.SetAsync(id, new FeatureState(true, null), ct)
                        .ConfigureAwait(false);
                }
                catch (FeatureRegistrationException ex)
                {
                    await stateStore.SetAsync(
                            id,
                            new FeatureState(false, ex.Message),
                            ct)
                        .ConfigureAwait(false);
                    return RegistrarFailed(ex.Message);
                }

                var snapshot = await stateStore.GetAllAsync(ct).ConfigureAwait(false);
                var dto = await BuildDtoAsync(metadata, snapshot, credentials, factories, ct)
                    .ConfigureAwait(false);
                return Results.Ok(dto);
            })
            .WithName("EnableFeature")
            .WithSummary("Enable a feature")
            .Produces<FeatureDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapPost("/{id}/disable", async (
                string id,
                IFeatureCatalog catalog,
                IFeatureStateStore stateStore,
                ICredentialStore credentials,
                IFeatureKernelRegistrar registrar,
                FeaturePluginFactoryRegistry factories,
                CancellationToken ct) =>
            {
                var metadata = catalog.Find(id);
                if (metadata is null)
                {
                    return Unknown(id);
                }

                if (registrar.IsRegistered(id))
                {
                    await registrar.UnregisterAsync(id, ct).ConfigureAwait(false);
                }

                await stateStore.SetAsync(id, new FeatureState(false, null), ct)
                    .ConfigureAwait(false);

                var snapshot = await stateStore.GetAllAsync(ct).ConfigureAwait(false);
                var dto = await BuildDtoAsync(metadata, snapshot, credentials, factories, ct)
                    .ConfigureAwait(false);
                return Results.Ok(dto);
            })
            .WithName("DisableFeature")
            .WithSummary("Disable a feature")
            .Produces<FeatureDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<FeatureDto> BuildDtoAsync(
        FeatureMetadata metadata,
        IReadOnlyDictionary<string, FeatureState> persisted,
        ICredentialStore credentials,
        FeaturePluginFactoryRegistry factories,
        CancellationToken ct)
    {
        var state = persisted.TryGetValue(metadata.Id, out var s)
            ? s
            : new FeatureState(false, null);

        var hasCredentials = true;
        if (metadata.RequiresCredentials)
        {
            foreach (var field in metadata.CredentialFields.Where(IsRequired))
            {
                var key = $"scout/{metadata.Id}/{field.Key}";
                if (!await credentials.ContainsAsync(key, ct).ConfigureAwait(false))
                {
                    hasCredentials = false;
                    break;
                }
            }
        }

        var status = ResolveStatus(metadata, state, hasCredentials, factories);

        return new FeatureDto(
            Id: metadata.Id,
            DisplayName: metadata.DisplayName,
            Description: metadata.Description,
            RequiresCredentials: metadata.RequiresCredentials,
            HasCredentials: hasCredentials,
            Enabled: state.Enabled,
            Status: status,
            LastError: state.LastError,
            CredentialFields: metadata.CredentialFields,
            ScopeMatrix: metadata.ScopeMatrix,
            SetupSteps: metadata.SetupSteps);
    }

    // Plain "text" inputs are treated as optional metadata (e.g. the
    // Kubernetes context selector); any other kind ("password",
    // "textarea") is required for the feature to be considered ready.
    private static bool IsRequired(FeatureCredentialFieldDto field) =>
        !string.Equals(field.Kind, "text", StringComparison.Ordinal);

    private static string ResolveStatus(
        FeatureMetadata metadata,
        FeatureState state,
        bool hasCredentials,
        FeaturePluginFactoryRegistry factories)
    {
        if (state.LastError is not null)
        {
            return "error";
        }

        if (state.Enabled && factories.HasFactory(metadata.Id))
        {
            return "enabled";
        }

        if (metadata.RequiresCredentials && !hasCredentials)
        {
            return "needs-credentials";
        }

        return "disabled";
    }

    private static IResult Unknown(string id) =>
        Results.Json(
            new { code = "features/unknown", id },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InvalidCredentialBody(string message) =>
        Results.Json(
            new { code = "features/invalid-credential-body", message },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult CredentialsMissing(string id) =>
        Results.Json(
            new
            {
                code = "features/credentials-missing",
                id,
                message = "Add credentials before enabling this feature.",
            },
            statusCode: StatusCodes.Status412PreconditionFailed);

    private static IResult RegistrarFailed(string message) =>
        Results.Json(
            new { code = "features/registrar-failed", message },
            statusCode: StatusCodes.Status500InternalServerError);

    private static IResult NotYetImplemented(string id) =>
        Results.Json(
            new
            {
                code = "features/not-yet-implemented",
                id,
                message = "This feature's plugin has not been wired yet.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
}
