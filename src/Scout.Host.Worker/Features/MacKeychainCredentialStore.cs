using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Features;

/// <summary>
/// macOS Keychain backed <see cref="ICredentialStore"/>. Calls the
/// <c>security</c> CLI which ships with every supported macOS release.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private const string SecurityCli = "security";
    private const string ServiceName = "scout";

    private readonly ILogger<MacKeychainCredentialStore> _logger;
    private readonly Lazy<bool> _available;

    /// <summary>Build a macOS keychain credential store.</summary>
    public MacKeychainCredentialStore(ILogger<MacKeychainCredentialStore> logger)
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

        // -U upserts the entry; without it the call fails when the item exists.
        var result = await RunAsync(
            new[]
            {
                "add-generic-password",
                "-a",
                key,
                "-s",
                ServiceName,
                "-w",
                value,
                "-U",
            },
            ct).ConfigureAwait(false);

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
            new[] { "find-generic-password", "-a", key, "-s", ServiceName, "-w" },
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
                new[] { "delete-generic-password", "-a", key, "-s", ServiceName },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "security delete-generic-password failed for key {Key}", key);
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
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SecurityCli,
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
                FileName = SecurityCli,
                ArgumentList = { "-h" },
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
            // `security -h` exits non-zero on macOS but still proves the
            // binary is present. We treat any exit (vs. exception) as OK.
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "security CLI is not reachable on this host");
            return false;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput);
}
