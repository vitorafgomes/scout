using Scout.Llm.Abstractions;

namespace Scout.Plugin.GitHub.Tests.Fakes;

/// <summary>A write gate that answers what the test says and remembers every request and record.</summary>
internal sealed class RecordingWriteGate : IWriteGate, IApprovedActions
{
    public WriteGateDecision Decision { get; set; } = new(WriteGateDecisionKind.Queued, "Waiting for the owner.", "act-1");

    public List<(string FeatureId, string Scope, string Target, string Summary)> Requests { get; } = [];

    public List<(string FeatureId, string Scope, string Target, string Outcome, string? ActionId)> Records { get; } = [];

    public Queue<ApprovedAction?> Approved { get; } = new();

    public Exception? ClaimFailure { get; set; }

    public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
    {
        Requests.Add((featureId, scope, target, summary));
        return Task.FromResult(Decision);
    }

    public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken)
    {
        Records.Add((featureId, scope, target, outcome, actionId));
        return Task.CompletedTask;
    }

    public Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken)
    {
        if (ClaimFailure is not null)
        {
            throw ClaimFailure;
        }

        return Task.FromResult(Approved.Count == 0 ? null : Approved.Dequeue());
    }
}
