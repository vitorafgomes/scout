namespace Scout.Llm.Abstractions;

/// <summary>
/// Asks the owner's control plane whether a write the model requested may run, and
/// records how a write that ran ended. The control plane holds the feature catalog
/// and the live grants; a function only says which feature and scope it performs.
/// </summary>
public interface IWriteGate
{
    /// <summary>Ask whether a write may run now, must wait for the owner, or is refused.</summary>
    /// <param name="featureId">The catalog id of the feature the write belongs to.</param>
    /// <param name="scope">The catalog scope row the write performs.</param>
    /// <param name="target">What the write acts on.</param>
    /// <param name="summary">The function and its arguments, with values only for the declared summary arguments.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The control plane's decision.</returns>
    Task<WriteGateDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        CancellationToken cancellationToken);

    /// <summary>Record how an allowed write ended.</summary>
    /// <param name="featureId">The catalog id of the feature the write belongs to.</param>
    /// <param name="scope">The catalog scope row the write performed.</param>
    /// <param name="target">What the write acted on.</param>
    /// <param name="outcome"><c>succeeded</c>, or <c>failed: </c> and the exception type.</param>
    /// <param name="actionId">The approved action's id, when the write came from the queue.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the control plane recorded the write.</returns>
    Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        CancellationToken cancellationToken);
}

/// <summary>What the control plane decided about a requested write.</summary>
public enum WriteGateDecisionKind
{
    /// <summary>The feature holds the grant and the write may run now.</summary>
    Allowed,

    /// <summary>The write waits for the owner's approval in the desktop.</summary>
    Queued,

    /// <summary>The write may not run.</summary>
    Refused,
}

/// <summary>The control plane's answer to a requested write.</summary>
/// <param name="Kind">Allowed, queued or refused.</param>
/// <param name="Reason">Why, in words the model can relay to the user.</param>
/// <param name="ActionId">The queued action's id, when queued.</param>
public sealed record WriteGateDecision(WriteGateDecisionKind Kind, string Reason, string? ActionId);

/// <summary>
/// Declares that a kernel function performs a write: which feature it belongs to and
/// which catalog scope row it performs. It never declares its own access; the control
/// plane decides that on every call.
/// </summary>
/// <param name="PluginName">The kernel plugin's name.</param>
/// <param name="FunctionName">The kernel function's name.</param>
/// <param name="FeatureId">The catalog id of the feature.</param>
/// <param name="Scope">The catalog scope row.</param>
public sealed record WriteFunctionDeclaration(string PluginName, string FunctionName, string FeatureId, string Scope)
{
    /// <summary>
    /// The arguments whose values the owner sees in the request summary, each cut to 200 characters;
    /// every other argument shows its name only. Empty by default.
    /// </summary>
    public IReadOnlyList<string> SummaryArguments { get; init; } = [];
}
