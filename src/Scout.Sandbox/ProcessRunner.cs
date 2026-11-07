using System.Diagnostics;

namespace Scout.Sandbox;

/// <summary>One call to an executable: its arguments, one element each, and the whole environment it starts with.</summary>
internal sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>Starts a process and hands over what it writes; the seam the tests use to record the exact argv.</summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="call"/> to its end and answers its exit code; on cancellation kills it and throws
    /// <see cref="OperationCanceledException"/>; throws <see cref="System.ComponentModel.Win32Exception"/> when the
    /// executable cannot start.
    /// </summary>
    Task<int> RunAsync(ProcessCall call, Action<string> onOutput, CancellationToken ct);
}

/// <summary>
/// <see cref="IProcessRunner"/> over <see cref="Process"/>: the environment is cleared before the call's is set, and
/// stdout and stderr are read in chunks, so a line without an end never sits whole in memory.
/// </summary>
internal sealed class SystemProcessRunner : IProcessRunner
{
    public static readonly SystemProcessRunner Instance = new();

    public async Task<int> RunAsync(ProcessCall call, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = call.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in call.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment.Clear();
        foreach (var (key, value) in call.Environment)
        {
            psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, onOutput), PumpAsync(process.StandardError, onOutput));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        await pumps.ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            onOutput(new string(buffer, 0, read));
        }
    }
}
