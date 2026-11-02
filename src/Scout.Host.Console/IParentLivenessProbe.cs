namespace Scout.Host.Console;

/// <summary>
/// Observes the liveness of the supervising parent process. The returned task
/// completes when the parent is gone, letting the watchdog stop the host before
/// it is reparented to init and orphaned.
/// </summary>
internal interface IParentLivenessProbe
{
    /// <summary>
    /// Completes when the parent process has exited. Honors <paramref name="cancellationToken"/>
    /// so a normal host shutdown cancels the wait instead of blocking.
    /// </summary>
    Task WaitForParentExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Detects parent death via the inherited stdin pipe: when the parent dies it
/// closes the write end, so a read returns 0 bytes (EOF). A read failure is
/// treated the same way (the pipe is no longer usable). Only valid when the
/// supervisor connects stdin as a pipe; standalone runs must not enable this.
/// </summary>
internal sealed class StdinEofParentLivenessProbe : IParentLivenessProbe
{
    public async Task WaitForParentExitAsync(CancellationToken cancellationToken)
    {
        var stdin = System.Console.OpenStandardInput();
        var buffer = new byte[256];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stdin.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown cancelled the wait; not a parent-death signal.
                throw;
            }
            catch
            {
                // Pipe broken: treat as parent gone.
                return;
            }

            // EOF: the parent closed the write end of the pipe by exiting.
            if (read == 0)
            {
                return;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Detects parent death through another reader of the stdin pipe: completes when
/// the task <paramref name="inputClosed"/> returns completes, which the local
/// adapter does on end of file or a read failure. Used when that adapter owns stdin,
/// because two readers would split its bytes.
/// </summary>
internal sealed class InputClosedParentLivenessProbe(Func<Task> inputClosed) : IParentLivenessProbe
{
    public Task WaitForParentExitAsync(CancellationToken cancellationToken)
        => inputClosed().WaitAsync(cancellationToken);
}
