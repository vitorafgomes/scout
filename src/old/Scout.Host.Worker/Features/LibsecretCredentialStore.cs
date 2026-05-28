using Scout.Core.Features;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Features;

/// <summary>
/// libsecret-backed <see cref="ICredentialStore"/> for Linux. Calls the
/// <c>secret-tool</c> CLI as a child process. The class is gated on
/// availability probing so it degrades to a no-op when libsecret is not
/// installed (CI runners, headless containers).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LibsecretCredentialStore : ICredentialStore
{
    private const string SecretTool = "secret-tool";

    private readonly ILogger<LibsecretCredentialStore> _logger;
    private readonly Lazy<bool> _available;

    /// <summary>Build a Linux libsecret credential store.</summary>
    public LibsecretCredentialStore(ILogger<LibsecretCredentialStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _available = new Lazy<bool>(ProbeAvailability);
    }

    /// <inheritdoc/>
    public bool IsAvailable => _available.Value;

    /// <inheritdoc/>
    public async Task<bool> StoreAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (!IsAvailable)
        {
            return false;
        }

        var args = new[]
        {
            "store",
            "--label",
            $"Scout: {key}",
            "scout-key",
            key,
        };

        var result = await RunAsync(args, stdin: value, ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!IsAvailable)
        {
            return null;
        }

        var result = await RunAsync(
            new[] { "lookup", "scout-key", key },
            stdin: null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var output = result.StandardOutput;
        if (output.EndsWith('\n'))
        {
            output = output[..^1];
        }

        return output;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!IsAvailable)
        {
            return;
        }

        try
        {
            await RunAsync(
                new[] { "clear", "scout-key", key },
                stdin: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "secret-tool clear failed for key {Key}", key);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsAsync(string key, CancellationToken ct)
    {
        var value = await GetAsync(key, ct).ConfigureAwait(false);
        return value is not null;
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? stdin,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SecretTool,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdout = new StringBuilder();
        var readerTask = Task.Run(
            async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    stdout.Append(line).Append('\n');
                }
            },
            ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await readerTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout.ToString());
    }

    private bool ProbeAvailability()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = SecretTool,
                ArgumentList = { "--help" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (probe is null)
            {
                return false;
            }

            probe.WaitForExit(2000);
            return probe.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "secret-tool is not reachable on this host");
            return false;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput);
}
