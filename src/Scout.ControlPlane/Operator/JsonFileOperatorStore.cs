using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Features;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The operator's state in <c>mandate.json</c> and <c>projects.json</c> beside the feature state
/// file. Every read-check-write runs under one gate, and writes go through a temp file and an
/// atomic move. A file that does not parse is moved aside to <c>&lt;name&gt;.corrupt-&lt;UTC
/// yyyyMMddHHmmss&gt;</c>, never overwritten; a file that cannot be opened or read fails the call
/// and stays where it is.
/// </summary>
public sealed class JsonFileOperatorStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<JsonFileOperatorStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build the store next to the feature state file.</summary>
    public JsonFileOperatorStore(
        IOptions<FeatureStateStoreOptions> options,
        ILogger<JsonFileOperatorStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var directory = Path.GetDirectoryName(options.Value.ResolveFilePath()) ?? string.Empty;
        MandatePath = Path.Combine(directory, "mandate.json");
        ProjectsPath = Path.Combine(directory, "projects.json");
    }

    /// <summary>Absolute path of the mandate file.</summary>
    public string MandatePath { get; }

    /// <summary>Absolute path of the projects file.</summary>
    public string ProjectsPath { get; }

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
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            var asidePath = path + ".corrupt-"
                + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(path, asidePath, overwrite: false);
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
