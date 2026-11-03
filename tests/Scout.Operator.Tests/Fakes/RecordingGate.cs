using Scout.Llm.Abstractions;

namespace Scout.Operator.Tests.Fakes;

/// <summary>An <see cref="IWriteGate"/> that answers every request with <see cref="Decision"/> and keeps every request and record.</summary>
internal sealed class RecordingGate : IWriteGate
{
    public WriteGateDecision Decision { get; set; } = new(WriteGateDecisionKind.Allowed, string.Empty, null);

    public List<(string Feature, string Scope, string Target, string Summary)> Requests { get; } = [];

    public List<(string Feature, string Scope, string Target, string Outcome, string? ActionId)> Records { get; } = [];

    public Exception? RecordFailure { get; set; }

    public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
    {
        Requests.Add((featureId, scope, target, summary));
        return Task.FromResult(Decision);
    }

    public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken)
    {
        if (RecordFailure is not null)
        {
            return Task.FromException(RecordFailure);
        }

        Records.Add((featureId, scope, target, outcome, actionId));
        return Task.CompletedTask;
    }
}
