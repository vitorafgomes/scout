using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    var service = host.Services.GetRequiredService<IndexingService>();
    var logger = host.Services.GetRequiredService<ILogger<IndexingService>>();

    try
    {
        var summary = await service
            .RunAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
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
