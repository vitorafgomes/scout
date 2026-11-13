using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The operator's state in <c>mandate.json</c>, <c>projects.json</c>, <c>readiness.json</c> and
/// <c>comments.json</c> beside the feature state file, and the projects' media under <c>operator/</c> there. Every read-check-write runs under one gate, and writes go through a temp file and an
/// atomic move. A file that does not parse is moved aside to <c>&lt;name&gt;.corrupt-&lt;UTC
/// yyyyMMddHHmmss&gt;</c>, never overwritten; a file that cannot be opened or read fails the call
/// and stays where it is.
/// </summary>
public sealed class JsonFileOperatorStore : IDisposable
{
    private const string CommentsNotSaved = "Scout could not save the comments in its data folder.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<JsonFileOperatorStore> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build the store next to the feature state file.</summary>
    public JsonFileOperatorStore(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonFileOperatorStore> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        var directory = Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty;
        MandatePath = Path.Combine(directory, "mandate.json");
        ProjectsPath = Path.Combine(directory, "projects.json");
        ReadinessPath = Path.Combine(directory, "readiness.json");
        CommentsPath = Path.Combine(directory, "comments.json");
        MediaRoot = Path.Combine(directory, "operator");
    }

    /// <summary>Absolute path of the mandate file.</summary>
    public string MandatePath { get; }

    /// <summary>Absolute path of the projects file.</summary>
    public string ProjectsPath { get; }

    /// <summary>Absolute path of the worker's latest readiness report.</summary>
    public string ReadinessPath { get; }

    /// <summary>Absolute path of the comments file.</summary>
    public string CommentsPath { get; }

    /// <summary>Absolute path of the folder that holds one media directory per project.</summary>
    public string MediaRoot { get; }

    /// <summary>The saved mandate, or null when the owner has not written one.</summary>
    public async Task<Mandate?> ReadMandateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync<Mandate>(MandatePath, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replace the mandate.</summary>
    public async Task WriteMandateAsync(Mandate mandate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mandate);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(MandatePath, mandate, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The worker's latest readiness report, or null when it has not reported.</summary>
    public async Task<Readiness?> ReadReadinessAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync<Readiness>(ReadinessPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replace the readiness report.</summary>
    public async Task WriteReadinessAsync(Readiness readiness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(ReadinessPath, readiness, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every project in the order they were created.</summary>
    public async Task<IReadOnlyList<OperatorProject>> ReadProjectsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync<List<OperatorProject>>(ProjectsPath, ct).ConfigureAwait(false) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read the projects, let <paramref name="change"/> check and change the list, and write it
    /// back, all under the gate. When <paramref name="change"/> throws or answers null, nothing is written.
    /// </summary>
    public async Task<T> UpdateProjectsAsync<T>(Func<List<OperatorProject>, T> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var projects = await ReadAsync<List<OperatorProject>>(ProjectsPath, ct).ConfigureAwait(false) ?? [];
            var result = change(projects);
            if (result is not null)
            {
                await WriteAsync(ProjectsPath, projects, ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The stored comments, or the empty state before the first sync.</summary>
    public async Task<CommentsState> ReadCommentsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync<CommentsState>(CommentsPath, ct).ConfigureAwait(false) ?? CommentsState.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read the comments, let <paramref name="change"/> check them and answer the new state, and write it back, all
    /// under the gate; the state after the call is returned. When <paramref name="change"/> throws or answers null,
    /// nothing is written.
    /// </summary>
    /// <exception cref="OperatorStoreException">The comments file could not be written; the earlier one stays.</exception>
    public async Task<CommentsState> UpdateCommentsAsync(Func<CommentsState, CommentsState?> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = await ReadAsync<CommentsState>(CommentsPath, ct).ConfigureAwait(false) ?? CommentsState.Empty;
            if (change(stored) is not { } next)
            {
                return stored;
            }

            try
            {
                await WriteAsync(CommentsPath, next, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OperatorStoreException(CommentsNotSaved, ex);
            }

            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new JsonException("The operator file holds null.");
        }
        catch (JsonException ex)
        {
            var asidePath = CorruptFile.MoveAside(path, _time);
            _logger.LogWarning(
                ex,
                "Operator file at {Path} is malformed; moved it to {AsidePath} and started empty",
                path,
                asidePath);
            return null;
        }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
