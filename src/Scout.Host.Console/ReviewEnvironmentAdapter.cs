using Scout.Audit;

namespace Scout.Host.Console;

/// <summary>
/// The audit library's <see cref="IReviewEnvironment"/> over the host's configuration: Docker runs are on when Scout
/// Desktop set <see cref="SandboxRunnerAdapter.EnabledKey"/> to true, the same switch the sandbox runner obeys.
/// </summary>
internal sealed class ReviewEnvironmentAdapter(bool containerRunsOn) : IReviewEnvironment
{
    public bool ContainerRunsOn => containerRunsOn;
}
