namespace Scout.Plugin.GitHub;

/// <summary>How one run ended after the owner asked it to stop: the conversation it was asked from, and whether the stop cut it short.</summary>
/// <param name="Origin">The conversation the run was asked from, or null when it was asked outside a chat.</param>
/// <param name="Stopped">True when the stop cut the run short; false when the run had already finished and its result stands.</param>
public sealed record StoppedRun(string? Origin, bool Stopped);

/// <summary>
/// The pull request test runs in progress in this worker, keyed by owner, repository and pull request number, so the owner
/// can stop them from Scout Desktop. A stop cancels a run's token and waits for the run to end; what the run did decides how
/// it ended: cut short by the stop, with no result kept, or finished, with its result standing, as when the sandbox had
/// already returned. The turn, the sandbox's record of the run and the answer to the stop always agree.
/// </summary>
public sealed class RunningTests
{
    private readonly Lock _gate = new();
    private readonly List<Run> _runs = [];

    /// <summary>
    /// Ask every run in progress of <paramref name="owner"/>/<paramref name="repo"/>#<paramref name="number"/>, whatever its
    /// case, to stop, and wait until each has ended; answers how each ended, none when no run of that pull request is in progress.
    /// </summary>
    public async Task<IReadOnlyList<StoppedRun>> StopAsync(string owner, string repo, int number, CancellationToken ct)
    {
        List<Run> asked;
        lock (_gate)
        {
            asked = [.. _runs.Where(r => r.Is(owner, repo, number) && r.AskToStop())];
        }

        foreach (var run in asked)
        {
            run.Cancel();
        }

        var ended = await Task.WhenAll(asked.Select(r => r.Ended.WaitAsync(ct))).ConfigureAwait(false);
        return [.. asked.Zip(ended, (run, stopped) => new StoppedRun(run.Origin, stopped))];
    }

    /// <summary>
    /// Track a run of <paramref name="owner"/>/<paramref name="repo"/>#<paramref name="number"/> asked from
    /// <paramref name="origin"/>; its token is cancelled by <paramref name="turn"/> or by a stop. Dispose it when the run ends.
    /// </summary>
    internal Run Start(string owner, string repo, int number, string? origin, CancellationToken turn)
    {
        var run = new Run(this, owner, repo, number, origin, turn);
        lock (_gate)
        {
            _runs.Add(run);
        }

        return run;
    }

    /// <summary>One run in progress: its token, whether the owner asked it to stop, and how it ended.</summary>
    internal sealed class Run : IDisposable
    {
        private readonly RunningTests _registry;
        private readonly string _owner;
        private readonly string _repo;
        private readonly int _number;
        private readonly CancellationTokenSource _cancel;
        private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _stopAsked;

        internal Run(RunningTests registry, string owner, string repo, int number, string? origin, CancellationToken turn)
        {
            _registry = registry;
            _owner = owner;
            _repo = repo;
            _number = number;
            Origin = origin;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(turn);
        }

        /// <summary>The conversation the run was asked from, or null.</summary>
        internal string? Origin { get; }

        /// <summary>Cancelled when the turn ends or the owner stops the run.</summary>
        internal CancellationToken Token => _cancel.Token;

        /// <summary>True once the owner asked the run to stop.</summary>
        internal bool StopAsked
        {
            get
            {
                lock (_registry._gate)
                {
                    return _stopAsked;
                }
            }
        }

        /// <summary>Completes when the run ended: true when the stop cut it short, false when it finished.</summary>
        internal Task<bool> Ended => _ended.Task;

        /// <summary>The run ended: <paramref name="stopped"/> when the stop cut it short; the first call decides.</summary>
        internal void End(bool stopped) => _ended.TrySetResult(stopped);

        /// <summary>
        /// Remove the run; one that never said how it ended was cut short when the owner had asked it to stop, since only the
        /// sandbox's return marks a run finished.
        /// </summary>
        public void Dispose()
        {
            bool stopAsked;
            lock (_registry._gate)
            {
                _registry._runs.Remove(this);
                stopAsked = _stopAsked;
            }

            _ended.TrySetResult(stopAsked);
            _cancel.Dispose();
        }

        internal bool Is(string owner, string repo, int number) =>
            number == _number && string.Equals(owner, _owner, StringComparison.OrdinalIgnoreCase) && string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase);

        internal bool AskToStop()
        {
            if (_stopAsked || _ended.Task.IsCompleted)
            {
                return false;
            }

            _stopAsked = true;
            return true;
        }

        internal void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
