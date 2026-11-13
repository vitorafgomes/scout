using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests.Guard;

internal sealed class FakeWriteGate(Func<WriteGateDecision> decide) : IWriteGate
{
    public List<(string FeatureId, string Scope, string Target, string Summary)> Requests { get; } = [];

    public List<(string FeatureId, string Scope, string Target, string Outcome, string? ActionId)> Records { get; } = [];

    public Exception? RecordFailure { get; set; }

    public static FakeWriteGate Answering(WriteGateDecisionKind kind, string reason = "", string? actionId = null) =>
        new(() => new WriteGateDecision(kind, reason, actionId));

    public static FakeWriteGate Throwing(Exception exception) => new(() => throw exception);

    public Task<WriteGateDecision> RequestAsync(
        string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
    {
        Requests.Add((featureId, scope, target, summary));
        return Task.FromResult(decide());
    }

    public List<string?> Payloads { get; } = [];

    public Task<WriteGateDecision> RequestAsync(
        string featureId, string scope, string target, string summary, string? payload, CancellationToken cancellationToken)
    {
        Payloads.Add(payload);
        return RequestAsync(featureId, scope, target, summary, cancellationToken);
    }

    public Task RecordAsync(
        string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken)
    {
        Records.Add((featureId, scope, target, outcome, actionId));
        return RecordFailure is null ? Task.CompletedTask : Task.FromException(RecordFailure);
    }
}
