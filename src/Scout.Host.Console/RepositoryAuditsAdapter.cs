using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The GitHub plugin's <see cref="IRepositoryAudits"/> over the audit library's, so the plugin's start, pause, stop and
/// status functions reach the audit job without either library referencing the other.
/// </summary>
internal sealed class RepositoryAuditsAdapter(Scout.Audit.IRepositoryAudits audits) : IRepositoryAudits
{
    public Task<string> StartAsync(string owner, string repo, CancellationToken ct) => audits.StartAsync(owner, repo, ct);

    public Task<string> PauseAsync(string owner, string repo, CancellationToken ct) => audits.PauseAsync(owner, repo, ct);

    public Task<string> StopAsync(string owner, string repo, CancellationToken ct) => audits.StopAsync(owner, repo, ct);

    public Task<string> StatusAsync(string owner, string repo, CancellationToken ct) => audits.StatusAsync(owner, repo, ct);
}
