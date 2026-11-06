namespace Scout.Llm.Abstractions;

/// <summary>
/// For a feature that runs its own approved actions in the worker: claims, from the owner's control plane, the
/// oldest action of the feature the owner approved on the Review screen. The claim is a lease; a run that does
/// not record the action within it lets a later run take the action again.
/// </summary>
public interface IApprovedActions
{
    /// <summary>The next approved action of <paramref name="featureId"/>, claimed for this run, or null when none waits.</summary>
    Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken);
}

/// <summary>An action the owner approved, as the control plane queued it when the write was requested.</summary>
/// <param name="Id">The action's id, which the record of the write names.</param>
/// <param name="FeatureId">The feature the action belongs to.</param>
/// <param name="Scope">The catalog scope row the action performs.</param>
/// <param name="Target">What the action acts on, as the request named it.</param>
/// <param name="Summary">The request summary the owner approved.</param>
public sealed record ApprovedAction(string Id, string FeatureId, string Scope, string Target, string Summary);
