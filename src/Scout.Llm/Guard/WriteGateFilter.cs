using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Guard;

/// <summary>
/// The enforcement point for writes: every invocation of a kernel function declared
/// as a write asks the control plane first, through <see cref="IWriteGate"/>. Allowed
/// runs the function and records its outcome; queued and refused never run it and
/// hand the model the reason instead; a gate that cannot answer refuses. A function that
/// throws <see cref="WriteFailedException"/> did not write: its message becomes the
/// function's answer and its outcome goes to the log after <c>failed:</c>. The request
/// summary names the function's own parameters and shows the values of the declared summary arguments only; a payload
/// declaration also sends those parameters' arguments as JSON, kept by the control plane only with a write it queues. An
/// argument the function does not declare reaches neither the summary nor the payload. Undeclared functions run unchanged.
/// </summary>
internal sealed class WriteGateFilter(
    IReadOnlyCollection<WriteFunctionDeclaration> declarations,
    IWriteGate gate,
    ILogger<WriteGateFilter> logger) : IFunctionInvocationFilter
{
    internal const string Unreachable =
        "Scout could not reach its control plane, so this write was not performed.";

    private const int MaxValueLength = 200;

    private readonly IReadOnlyCollection<WriteFunctionDeclaration> _declarations =
        declarations ?? throw new ArgumentNullException(nameof(declarations));
    private readonly IWriteGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly ILogger<WriteGateFilter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    internal static string Queued(string? actionId) =>
        $"Queued for the owner's approval: an Approve card is waiting in the chat and in Approvals of Scout Desktop (action {actionId}).";

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var declaration = _declarations.FirstOrDefault(d =>
            d.PluginName == context.Function.PluginName && d.FunctionName == context.Function.Name);
        if (declaration is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var target = $"{declaration.PluginName}.{declaration.FunctionName}";
        WriteGateDecision decision;
        try
        {
            decision = await _gate.RequestAsync(
                declaration.FeatureId, declaration.Scope, target, Summarize(context, declaration),
                declaration.CarriesPayload ? Payload(context) : null, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The write gate did not answer for {Function} ({ExceptionType}); the write was not performed.",
                target, ex.GetType().Name);
            context.Result = new FunctionResult(context.Function, Unreachable);
            return;
        }

        switch (decision.Kind)
        {
            case WriteGateDecisionKind.Allowed:
                await RunAndRecordAsync(context, next, declaration, target, decision.ActionId).ConfigureAwait(false);
                return;
            case WriteGateDecisionKind.Queued:
                context.Result = new FunctionResult(context.Function, Queued(decision.ActionId));
                return;
            default:
                context.Result = new FunctionResult(context.Function, decision.Reason);
                return;
        }
    }

    private async Task RunAndRecordAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next,
        WriteFunctionDeclaration declaration,
        string target,
        string? actionId)
    {
        var outcome = "succeeded";
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (WriteFailedException ex)
        {
            outcome = $"failed: {ex.Outcome}";
            context.Result = new FunctionResult(context.Function, ex.Message);
        }
        catch (Exception ex)
        {
            outcome = $"failed: {ex.GetType().Name}";
            throw;
        }
        finally
        {
            try
            {
                await _gate.RecordAsync(
                    declaration.FeatureId, declaration.Scope, target, outcome, actionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "The write gate did not record {Function} ({ExceptionType}).", target, ex.GetType().Name);
            }
        }
    }

    private static string Summarize(FunctionInvocationContext context, WriteFunctionDeclaration declaration)
    {
        var arguments = Declared(context)
            .Select(argument => declaration.SummaryArguments.Contains(argument.Key, StringComparer.Ordinal)
                ? $"{argument.Key}={Truncate(argument.Value)}"
                : argument.Key);
        return $"{context.Function.Name}({string.Join(", ", arguments)})";
    }

    private static string Payload(FunctionInvocationContext context) =>
        JsonSerializer.Serialize(Declared(context)
            .ToDictionary(argument => argument.Key, argument => argument.Value?.ToString(), StringComparer.Ordinal));

    private static IEnumerable<KeyValuePair<string, object?>> Declared(FunctionInvocationContext context)
    {
        var declared = context.Function.Metadata.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        return context.Arguments
            .Where(argument => declared.Contains(argument.Key))
            .OrderBy(argument => argument.Key, StringComparer.Ordinal);
    }

    private static string Truncate(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Length <= MaxValueLength ? text : text[..MaxValueLength];
    }
}
