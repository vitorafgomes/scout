namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Starts, pauses, stops and reports on the audit of a whole repository Scout runs as a job with saved progress, in sentences the
/// model relays to the owner; the host provides it.
/// </summary>
public interface IRepositoryAudits
{
    /// <summary>Start, or resume, the audit of <paramref name="owner"/>/<paramref name="repo"/> and answer with its plan, or why it did not start.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> StartAsync(string owner, string repo, CancellationToken ct);

    /// <summary>Pause the repository's running audit; an area in progress finishes first, the findings so far stay and a start resumes it.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the pause.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> PauseAsync(string owner, string repo, CancellationToken ct);

    /// <summary>Stop the repository's running or paused audit; an area in progress finishes first and the findings so far stay.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the stop.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> StopAsync(string owner, string repo, CancellationToken ct);

    /// <summary>How the repository's latest audit is going.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> StatusAsync(string owner, string repo, CancellationToken ct);
}
