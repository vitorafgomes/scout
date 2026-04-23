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
/// Shutdown cancels the ingest via a dedicated CTS so the background task
/// stops cleanly instead of running against disposed dependencies.
/// </summary>
internal sealed class CodeQAStartupIndexer : IHostedService
{
    private readonly IndexingService _indexingService;
    private readonly CodeQAOptions _options;
    private readonly ILogger<CodeQAStartupIndexer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _indexTask;

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
        // ingest. The task handle is retained so StopAsync can await it
        // and the dedicated CTS so shutdown can cancel cleanly.
        _indexTask = Task.Run(() => RunOnceAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts.Cancel();

            if (_indexTask is { } task)
            {
                try
                {
                    await Task
                        .WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host shutdown timeout elapsed before the ingest
                    // finished draining; nothing we can do here.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CodeQA startup index did not stop cleanly");
                }
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _indexingService
                .RunAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "CodeQA startup index completed: files={Files}, chunks={Chunks}, vectors={Vectors}, elapsed={Elapsed}",
                summary.FilesIngested,
                summary.ChunksProduced,
                summary.VectorsWritten,
                summary.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("CodeQA startup index cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CodeQA startup index failed; the worker keeps running.");
        }
    }
}
