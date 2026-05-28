using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// <c>/providers</c> endpoint surface. Ollama is fully wired (status +
/// lifecycle); LM Studio returns 501 on lifecycle endpoints since its
/// daemon lifecycle is not under the worker's control.
/// </summary>
public static class ProvidersEndpoints
{
    private const string OllamaId = "ollama";
    private const string LmStudioId = "lm-studio";

    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    private static ProviderStatus LmStudioStub() => new(
        Id: LmStudioId,
        Installed: false,
        Running: false,
        Endpoint: "http://localhost:1234",
        Version: null,
        ManagedByWorker: false,
        Autostart: true,
        SupportsLifecycle: false,
        LastError: null,
        Models: Array.Empty<ProviderModel>());

    public static IEndpointRouteBuilder MapProvidersApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/providers").WithTags("Providers");

        group.MapGet("/", async (OllamaProviderService ollama, CancellationToken ct) =>
            {
                var ollamaStatus = await ollama.GetStatusAsync(ct).ConfigureAwait(false);
                return Results.Ok(new[] { ollamaStatus, LmStudioStub() });
            })
            .WithName("ListProviders")
            .WithSummary("List known local runtime providers")
            .WithDescription("Returns a snapshot for each provider the worker knows about. Ollama is live; LM Studio is a stub.")
            .Produces<ProviderStatus[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}/status", async (string id, OllamaProviderService ollama, CancellationToken ct) =>
            {
                ProviderStatus? status = id switch
                {
                    OllamaId => await ollama.GetStatusAsync(ct).ConfigureAwait(false),
                    LmStudioId => LmStudioStub(),
                    _ => null,
                };

                return status is null
                    ? Results.Json(new { code = "providers/unknown", id }, statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(status);
            })
            .WithName("GetProviderStatus")
            .WithSummary("Get a single provider snapshot")
            .WithDescription("Returns the live status for Ollama, a stub for LM Studio, or 404 when the id is not known.")
            .Produces<ProviderStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/start", async (string id, OllamaProviderService ollama, CancellationToken ct) =>
            {
                if (id == OllamaId)
                {
                    try
                    {
                        var status = await ollama.StartAsync(ct).ConfigureAwait(false);
                        return Results.Ok(status);
                    }
                    catch (ProviderNotReadyException ex)
                    {
                        return Results.Json(
                            new { code = "providers/not-ready", message = ex.Message },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    catch (ProviderStartTimeoutException ex)
                    {
                        return Results.Json(
                            new { code = "providers/start-timeout", message = ex.Message },
                            statusCode: StatusCodes.Status504GatewayTimeout);
                    }
                    catch (ProviderStartFailedException ex)
                    {
                        return Results.Json(
                            new { code = "providers/start-failed", message = ex.Message },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                if (id == LmStudioId)
                {
                    return Results.Json(
                        new
                        {
                            code = "providers/lifecycle-unsupported",
                            message = "LM Studio lifecycle control is not available on this install",
                        },
                        statusCode: StatusCodes.Status501NotImplemented);
                }

                return Results.Json(
                    new { code = "providers/unknown", id },
                    statusCode: StatusCodes.Status404NotFound);
            })
            .WithName("StartProvider")
            .WithSummary("Start a provider")
            .WithDescription("Starts the local runtime if supported. Returns 501 for providers without lifecycle support.")
            .Produces<ProviderStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        group.MapPost("/{id}/restart", async (string id, OllamaProviderService ollama, CancellationToken ct) =>
            {
                if (id == OllamaId)
                {
                    try
                    {
                        var status = await ollama.RestartAsync(ct).ConfigureAwait(false);
                        return Results.Ok(status);
                    }
                    catch (ProviderNotReadyException ex)
                    {
                        return Results.Json(
                            new { code = "providers/not-ready", message = ex.Message },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    catch (ProviderNotManagedException ex)
                    {
                        return Results.Json(
                            new { code = "providers/not-managed", message = ex.Message },
                            statusCode: StatusCodes.Status409Conflict);
                    }
                    catch (ProviderStartTimeoutException ex)
                    {
                        return Results.Json(
                            new { code = "providers/start-timeout", message = ex.Message },
                            statusCode: StatusCodes.Status504GatewayTimeout);
                    }
                    catch (ProviderStartFailedException ex)
                    {
                        return Results.Json(
                            new { code = "providers/start-failed", message = ex.Message },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                if (id == LmStudioId)
                {
                    return Results.Json(
                        new
                        {
                            code = "providers/lifecycle-unsupported",
                            message = "LM Studio lifecycle control is not available on this install",
                        },
                        statusCode: StatusCodes.Status501NotImplemented);
                }

                return Results.Json(
                    new { code = "providers/unknown", id },
                    statusCode: StatusCodes.Status404NotFound);
            })
            .WithName("RestartProvider")
            .WithSummary("Restart a provider")
            .WithDescription("Restarts a worker-managed runtime. 409 when the daemon is not owned by the worker.")
            .Produces<ProviderStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        group.MapPost("/{id}/stop", async (string id, OllamaProviderService ollama, CancellationToken ct) =>
            {
                if (id == OllamaId)
                {
                    try
                    {
                        var status = await ollama.StopAsync(ct).ConfigureAwait(false);
                        return Results.Ok(status);
                    }
                    catch (ProviderNotReadyException ex)
                    {
                        return Results.Json(
                            new { code = "providers/not-ready", message = ex.Message },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    catch (ProviderStopFailedException ex)
                    {
                        return Results.Json(
                            new { code = "providers/stop-failed", message = ex.Message },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                if (id == LmStudioId)
                {
                    return Results.Json(
                        new
                        {
                            code = "providers/lifecycle-unsupported",
                            message = "LM Studio lifecycle control is not available on this install",
                        },
                        statusCode: StatusCodes.Status501NotImplemented);
                }

                return Results.Json(
                    new { code = "providers/unknown", id },
                    statusCode: StatusCodes.Status404NotFound);
            })
            .WithName("StopProvider")
            .WithSummary("Stop a provider")
            .WithDescription("Stops the local runtime if supported. Works for worker-owned and externally-started daemons.")
            .Produces<ProviderStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id}/models/{model}/pull", async (
                string id,
                string model,
                OllamaProviderService ollama,
                HttpContext ctx,
                CancellationToken ct) =>
            {
                if (id == LmStudioId)
                {
                    ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
                    await ctx.Response.WriteAsJsonAsync(
                        new
                        {
                            code = "providers/lifecycle-unsupported",
                            message = "LM Studio model pull is not available on this install",
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                if (id != OllamaId)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync(new { code = "providers/unknown", id }, ct)
                        .ConfigureAwait(false);
                    return;
                }

                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                try
                {
                    await foreach (var ev in ollama.PullModelAsync(model, ct).ConfigureAwait(false))
                    {
                        var payload = JsonSerializer.Serialize(ev, SseJsonOptions);
                        await ctx.Response.WriteAsync($"data: {payload}\n\n", ct).ConfigureAwait(false);
                        await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Client went away; nothing else to do.
                }
                catch (ProviderNotReadyException ex)
                {
                    var errPayload = JsonSerializer.Serialize(
                        new { code = "providers/not-ready", message = ex.Message },
                        SseJsonOptions);
                    await ctx.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", ct)
                        .ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    var errPayload = JsonSerializer.Serialize(
                        new { code = "providers/pull-failed", message = ex.Message },
                        SseJsonOptions);
                    await ctx.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", ct)
                        .ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            })
            .WithName("PullProviderModel")
            .WithSummary("Pull a model")
            .WithDescription("Streams Ollama pull progress as SSE events. Each event is a JSON document with status and optional digest/total/completed.")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapDelete("/{id}/models/{model}", async (
                string id,
                string model,
                OllamaProviderService ollama,
                CancellationToken ct) =>
            {
                if (id == OllamaId)
                {
                    try
                    {
                        await ollama.DeleteModelAsync(model, ct).ConfigureAwait(false);
                        return Results.NoContent();
                    }
                    catch (ProviderNotReadyException ex)
                    {
                        return Results.Json(
                            new { code = "providers/not-ready", message = ex.Message },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    catch (HttpRequestException ex)
                    {
                        return Results.Json(
                            new { code = "providers/delete-failed", message = ex.Message },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                if (id == LmStudioId)
                {
                    return Results.Json(
                        new
                        {
                            code = "providers/lifecycle-unsupported",
                            message = "LM Studio model delete is not available on this install",
                        },
                        statusCode: StatusCodes.Status501NotImplemented);
                }

                return Results.Json(
                    new { code = "providers/unknown", id },
                    statusCode: StatusCodes.Status404NotFound);
            })
            .WithName("DeleteProviderModel")
            .WithSummary("Delete a model")
            .WithDescription("Removes a model from the local runtime.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/{id}/autostart", async (string id, AutostartRequest body, ILocalRuntimeConfigStore store, CancellationToken ct) =>
            {
                if (id is not (OllamaId or LmStudioId))
                {
                    return Results.Json(
                        new { code = "providers/unknown", id },
                        statusCode: StatusCodes.Status404NotFound);
                }

                await store.SetAutostartAsync(id, body.Enabled, ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .WithName("SetProviderAutostart")
            .WithSummary("Toggle provider autostart")
            .WithDescription("Persists the desired autostart flag for a known provider.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
