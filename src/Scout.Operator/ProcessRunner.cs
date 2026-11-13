using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Scout.Operator;

/// <summary>What a finished program left: its exit code, its whole standard output and the end of its standard error.</summary>
internal sealed record ProcessResult(int ExitCode, string Output, string ErrorTail)
{
    /// <summary>The standard output as the program wrote it, byte for byte, when the run asked for raw output; empty otherwise.</summary>
    public byte[] OutputBytes { get; init; } = [];
}

/// <summary>Runs one external program to completion.</summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="program"/> with <paramref name="arguments"/> as separate arguments, never through a shell, and
    /// return how it ended. <paramref name="input"/> as UTF-8, else <paramref name="inputBytes"/> as they are, is written to its
    /// standard input, which is then closed; without either it is closed at once. With <paramref name="rawOutput"/> the
    /// standard output comes back only as bytes and <see cref="ProcessResult.Output"/> is empty. A program that cannot be started, or runs past
    /// <paramref name="timeout"/>, fails with an <see cref="InvalidOperationException"/>; a cancelled run is killed.
    /// </summary>
    Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, string? input = null, byte[]? inputBytes = null, bool rawOutput = false);
}

/// <summary>
/// <see cref="IProcessRunner"/> over <see cref="Process"/>: the arguments go as an argument list, standard input carries
/// only the given input, or is closed at once, so the program never reads the worker's own input; a program that stops
/// reading its input early still returns how it ended. Standard output and error are read while it runs, the
/// last <see cref="TailLength"/> characters of standard error are kept, and a run past its timeout or cancelled is killed
/// with every process it started.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    internal const int TailLength = 8192;

    internal static string NotStarted(string program) => $"Scout could not start {Path.GetFileName(program)}.";

    internal static string TimedOut(string program, TimeSpan timeout) =>
        $"{Path.GetFileName(program)} ran longer than {timeout.TotalSeconds:0} seconds and was stopped.";

    public async Task<ProcessResult> RunAsync(
        string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, string? input = null, byte[]? inputBytes = null, bool rawOutput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(NotStarted(program), ex);
        }

        var output = ReadAllAsync(process.StandardOutput);
        var error = ReadTailAsync(process.StandardError);
        var feed = FeedAsync(process.StandardInput, input is null ? inputBytes ?? [] : Encoding.UTF8.GetBytes(input));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, output, error).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(TimedOut(program, timeout));
        }

        await feed.ConfigureAwait(false);
        var bytes = await output.ConfigureAwait(false);
        if (rawOutput)
        {
            return new ProcessResult(process.ExitCode, string.Empty, await error.ConfigureAwait(false)) { OutputBytes = bytes };
        }

        using var text = new StreamReader(new MemoryStream(bytes), process.StandardOutput.CurrentEncoding, detectEncodingFromByteOrderMarks: true);
        return new ProcessResult(process.ExitCode, await text.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAllAsync(StreamReader reader)
    {
        using var buffer = new MemoryStream();
        await reader.BaseStream.CopyToAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task StopAsync(Process process, Task<byte[]> output, Task<string> error)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException or IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task FeedAsync(StreamWriter writer, byte[] input)
    {
        try
        {
            if (input.Length > 0)
            {
                await writer.BaseStream.WriteAsync(input, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > TailLength * 2)
            {
                tail.Remove(0, tail.Length - TailLength);
            }
        }

        return tail.Length > TailLength ? tail.ToString(tail.Length - TailLength, TailLength) : tail.ToString();
    }
}
