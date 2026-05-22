using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Scout.Adapter.Discord;
using Scout.Core.Hosting;
using Scout.Host.Worker;
using Scout.Host.Worker.Http;
using Scout.Host.Worker.Providers;
using Scout.Llm.Ollama;
using Scout.Plugin.Chat;
using Scout.Plugin.CloudStatus;
using Scout.Plugin.CodeQA;
using Scout.Plugin.Help;

var runIndex = args.Length > 0 && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase);

if (runIndex)
{
    // One-shot CLI path: stays on a plain Host (no web server). Wires only
    // the ingestion pipeline; no adapter, no worker, no chat plugins.
    var indexBuilder = Host.CreateApplicationBuilder(args);

    indexBuilder.Services
        .AddScout(indexBuilder.Configuration)
        .AddOllamaLlmProvider()
        .AddOllamaEmbedder()
        .AddHelpPlugin()
        .AddCodeQAPlugin(indexBuilder.Configuration)
        .AddSingleton<IndexingService>();

    using var host = indexBuilder.Build();
    var logger = host.Services.GetRequiredService<ILogger<IndexingService>>();

    // Pre-flight configuration check — fail with a targeted message
    // instead of the cryptic DI failure the empty registrations produce.
    var opts = host.Services.GetRequiredService<IOptions<CodeQAOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.Repository.Owner))
    {
        logger.LogError("CodeQA is not configured: set Scout:Plugins:CodeQA:Repository:Owner.");
        return 1;
    }

    var service = host.Services.GetRequiredService<IndexingService>();

    // host.RunAsync() is never called on the index path, so
    // ApplicationStopping never fires. Wire Ctrl+C to a dedicated CTS
    // so the one-shot ingest is actually cancellable.
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        var summary = await service
            .RunAsync(cts.Token)
            .ConfigureAwait(false);
        logger.LogInformation(
            "CodeQA index completed: files={Files}, chunks={Chunks}, vectors={Vectors}, elapsed={Elapsed}",
            summary.FilesIngested,
            summary.ChunksProduced,
            summary.VectorsWritten,
            summary.Elapsed);
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "CodeQA index failed");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

// Loopback only, ephemeral port. 127.0.0.1 (not "localhost") to avoid
// IPv6 resolution surprises in the supervisor's port parser.
builder.WebHost.UseUrls("http://127.0.0.1:0");

builder.Services
    .AddScout(builder.Configuration)
    .AddOllamaLlmProvider()
    .AddOllamaEmbedder()
    .AddHelpPlugin()
    .AddCodeQAPlugin(builder.Configuration);

builder.Services
    .AddDiscordAdapter(builder.Configuration)
    .AddCloudStatusPlugin(builder.Configuration)
    // Fallback chat router must come LAST: it streams any unmatched
    // message through the LLM, so every keyword/regex router needs to
    // run first.
    .AddLlmChatPlugin(builder.Configuration)
    .AddHostedService<ScoutWorker>();

var codeQAConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration["Scout:Plugins:CodeQA:Repository:Owner"]);

if (codeQAConfigured)
{
    builder.Services
        .AddSingleton<IndexingService>()
        .AddHostedService<CodeQAStartupIndexer>();
}

builder.Services.AddLocalProviders(builder.Configuration);

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Scout Worker API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

// Defence in depth: even though Kestrel binds to loopback, reject any
// connection whose remote address is not a loopback before routing.
app.UseMiddleware<LoopbackOnlyMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "Scout Worker API";
});
app.MapProvidersApi();

// Start the host explicitly so the SCOUT_HTTP marker can be printed AFTER
// Kestrel binds and BEFORE ScoutWorker's ExecuteAsync gets the chance to
// emit SCOUT_READY. The Electron supervisor relies on this ordering to
// learn the worker's HTTP port before considering it operational.
await app.StartAsync().ConfigureAwait(false);

var server = app.Services.GetRequiredService<IServer>();
var boundAddress = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
if (!string.IsNullOrWhiteSpace(boundAddress))
{
    var boundUri = new Uri(boundAddress);
    Console.WriteLine($"SCOUT_HTTP=127.0.0.1:{boundUri.Port}");
    Console.Out.Flush();
}

await app.WaitForShutdownAsync().ConfigureAwait(false);
return 0;

// Exposed for WebApplicationFactory<Program> in tests.
public partial class Program;
