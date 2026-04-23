using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;

namespace Scout.Host.Worker;

/// <summary>
/// One-shot background indexer run during host startup. Fires the
/// CodeQA ingestion pipeline on a background task so the host can reach
/// the chat adapter without waiting for indexing to finish, and swallows
/// any failure — a broken index must never prevent the bot from coming
/// online. When <see cref="RepositoryOptions.Owner"/> is empty the whole
/// step is skipped so a vanilla <c>appsettings.json</c> stays inert.
/// </summary>
internal sealed class CodeQAStartupIndexer : IHostedService
{
    private readonly IndexingService _indexingService;
    private readonly CodeQAOptions _options;
    private readonly ILogger<CodeQAStartupIndexer> _logger;

    public CodeQAStartupIndexer(
        IndexingService indexingService,
        IOptions<CodeQAOptions> options,
        ILogger<CodeQAStartupIndexer> logger)
    {
        _indexingService = indexingService ?? throw new ArgumentNullException(nameof(indexingService));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Repository.Owner))
        {
            _logger.LogInformation("CodeQA not configured — skipping startup index");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting CodeQA startup index");

        // Fire-and-forget so adapter startup is not blocked by a long
        // ingest. CancellationToken from StartAsync is not honoured past
        // this point because the ingest should survive host-startup
        // cancellation windows and run to completion or log failure.
        _ = Task.Run(RunOnceAsync, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunOnceAsync()
    {
        try
        {
            var summary = await _indexingService
                .RunAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "CodeQA startup index completed: files={Files}, chunks={Chunks}, vectors={Vectors}, elapsed={Elapsed}",
                summary.FilesIngested,
                summary.ChunksProduced,
                summary.VectorsWritten,
                summary.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CodeQA startup index failed; the worker keeps running.");
        }
    }
}
