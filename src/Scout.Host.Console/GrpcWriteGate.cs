using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Scout.ControlPlane.Contracts;
using Scout.Llm.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// <see cref="IWriteGate"/> over the control plane's <c>WorkerGate</c> service, on the
/// Unix socket the desktop passes, with the worker token the desktop passes as a bearer token.
/// Any decision other than <c>allowed</c> or <c>queued</c> is a refusal. Messages up to <see cref="MaxMessageBytes"/> go
/// either way, so a full comments view, 1000 comments of up to 2000 characters each, fits. A call whose token was cancelled,
/// as when the worker stops, ends as an <see cref="OperationCanceledException"/> carrying that token.
/// </summary>
internal sealed class GrpcWriteGate : IWriteGate, IApprovedActions, IJobControls, IDisposable
{
    internal const string RefusedWithoutReason = "The control plane refused this write.";

    internal const int MaxMessageBytes = 16 * 1024 * 1024;

    private readonly GrpcChannel _channel;
    private readonly WorkerGate.WorkerGateClient _client;
    private readonly Metadata _headers;
    private readonly TimeSpan _deadline;

    public GrpcWriteGate(string socketPath, string token, TimeSpan? deadline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = MaxMessageBytes,
            MaxSendMessageSize = MaxMessageBytes,
        });
        _client = new WorkerGate.WorkerGateClient(_channel);
        _headers = new Metadata { { "authorization", $"Bearer {token}" } };
        _deadline = deadline ?? TimeSpan.FromSeconds(10);
    }

    internal WorkerGate.WorkerGateClient Client => _client;

    internal CallOptions CallOptions(CancellationToken cancellationToken)
        => new(_headers, DateTime.UtcNow.Add(_deadline), cancellationToken);

    public Task<WriteGateDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        CancellationToken cancellationToken) => RequestAsync(featureId, scope, target, summary, null, cancellationToken);

    public async Task<WriteGateDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        string? payload,
        CancellationToken cancellationToken)
    {
        var request = new RequestWriteRequest { FeatureId = featureId, Scope = scope, Target = target, Summary = summary };
        if (payload is not null)
        {
            request.Payload = payload;
        }

        RequestWriteResponse reply;
        try
        {
            reply = await _client.RequestWriteAsync(
                request,
                _headers,
                DateTime.UtcNow.Add(_deadline),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, cancellationToken);
        }

        var actionId = reply.HasActionId ? reply.ActionId : null;
        return reply.Decision switch
        {
            "allowed" => new WriteGateDecision(WriteGateDecisionKind.Allowed, reply.Reason, actionId),
            "queued" => new WriteGateDecision(WriteGateDecisionKind.Queued, reply.Reason, actionId),
            _ => new WriteGateDecision(
                WriteGateDecisionKind.Refused,
                string.IsNullOrWhiteSpace(reply.Reason) ? RefusedWithoutReason : reply.Reason,
                null),
        };
    }

    public Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        CancellationToken cancellationToken) => RecordAsync(featureId, scope, target, outcome, actionId, null, cancellationToken);

    public Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        string? result,
        CancellationToken cancellationToken) => RecordAsync(featureId, scope, target, outcome, actionId, result, null, cancellationToken);

    public async Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        string? result,
        string? signedAs,
        CancellationToken cancellationToken)
    {
        var request = new RecordWriteRequest { FeatureId = featureId, Scope = scope, Target = target, Outcome = outcome };
        if (actionId is not null)
        {
            request.ActionId = actionId;
        }

        if (result is not null)
        {
            request.Result = result;
        }

        if (signedAs is not null)
        {
            request.SignedAs = signedAs;
        }

        try
        {
            await _client.RecordWriteAsync(request, _headers, DateTime.UtcNow.Add(_deadline), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, cancellationToken);
        }
    }

    public async Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken)
    {
        NextApprovedActionResponse reply;
        try
        {
            reply = await _client.NextApprovedActionAsync(
                new NextApprovedActionRequest { FeatureId = featureId }, _headers, DateTime.UtcNow.Add(_deadline), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, cancellationToken);
        }

        return reply.Action is not { } action
            ? null
            : new ApprovedAction(action.Id, action.FeatureId, action.Action, action.Target, action.Summary)
            {
                Payload = reply.HasPayload ? reply.Payload : null,
            };
    }

    public async Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return (await _client.CheckActionGrantAsync(
                    new CheckActionGrantRequest { FeatureId = action.FeatureId, ActionId = action.Id },
                    _headers,
                    DateTime.UtcNow.Add(_deadline),
                    cancellationToken)
                .ConfigureAwait(false)).Granted;
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, cancellationToken);
        }
    }

    public async Task<JobControl?> TakeNextAsync(CancellationToken ct)
    {
        NextJobControlResponse reply;
        try
        {
            reply = await _client.NextJobControlAsync(new NextJobControlRequest(), _headers, DateTime.UtcNow.Add(_deadline), ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, ct);
        }

        return reply.Control is not { } control
            ? null
            : new JobControl(control.Id, control.Kind, control.Owner, control.Repo, control.HasNumber ? control.Number : null, control.Action);
    }

    public async Task SettleAsync(string id, string status, string outcome, CancellationToken ct)
    {
        try
        {
            await _client.SettleJobControlAsync(new SettleJobControlRequest { Id = id, Outcome = outcome, Status = status }, _headers, DateTime.UtcNow.Add(_deadline), ct)
                .ConfigureAwait(false);
        }
        catch (RpcException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Status.Detail, ex, ct);
        }
    }

    public void Dispose() => _channel.Dispose();
}
