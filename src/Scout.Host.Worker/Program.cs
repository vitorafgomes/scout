using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Discord;
using Scout.Core.Hosting;
using Scout.Host.Worker;
using Scout.Llm.Ollama;
using Scout.Plugin.CloudStatus;
using Scout.Plugin.CodeQA;

var builder = Host.CreateApplicationBuilder(args);

var runIndex = args.Length > 0 && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase);

builder.Services
    .AddScout(builder.Configuration)
    .AddOllamaLlmProvider()
    .AddOllamaEmbedder()
    .AddCodeQAPlugin(builder.Configuration);

if (runIndex)
{
    // One-shot CLI path: wire only the ingestion pipeline, no adapter,
    // no worker, no chat plugins.
    builder.Services.AddSingleton<IndexingService>();

    using var host = builder.Build();
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

builder.Services
    .AddDiscordAdapter(builder.Configuration)
    .AddCloudStatusPlugin(builder.Configuration)
    .AddSingleton<IndexingService>()
    .AddHostedService<CodeQAStartupIndexer>()
    .AddHostedService<ScoutWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
