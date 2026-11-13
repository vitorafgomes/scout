namespace Scout.Audit;

/// <summary>
/// The facts of this computer a pull request review is checked against before it starts; the host adapts them from its
/// configuration, so this library never reads the sandbox's settings.
/// </summary>
public interface IReviewEnvironment
{
    /// <summary>Whether the owner has Docker runs on in Scout Desktop, so a review's tests may run in Scout's container.</summary>
    bool ContainerRunsOn { get; }
}
