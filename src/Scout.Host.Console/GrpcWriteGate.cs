using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Scout.ControlPlane.Contracts;
using Scout.Llm.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// <see cref="IWriteGate"/> over the control plane's <c>WorkerGate</c> service, on the
/// Unix socket the desktop passes, with the worker token the desktop passes as a bearer token.
/// Any decision other than <c>allowed</c> or <c>queued</c> is a refusal.
/// </summary>
internal sealed class GrpcWriteGate : IWriteGate, IDisposable
{
    internal const string RefusedWithoutReason = "The control plane refused this write.";

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
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        _client = new WorkerGate.WorkerGateClient(_channel);
        _headers = new Metadata { { "authorization", $"Bearer {token}" } };
        _deadline = deadline ?? TimeSpan.FromSeconds(10);
    }

    public async Task<WriteGateDecision> RequestAsync(
        string featureId,
        string scope,
        string target,
        string summary,
        CancellationToken cancellationToken)
    {
        var reply = await _client.RequestWriteAsync(
            new RequestWriteRequest { FeatureId = featureId, Scope = scope, Target = target, Summary = summary },
            _headers,
            DateTime.UtcNow.Add(_deadline),
            cancellationToken).ConfigureAwait(false);
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

    public async Task RecordAsync(
        string featureId,
        string scope,
        string target,
        string outcome,
        string? actionId,
        CancellationToken cancellationToken)
    {
        var request = new RecordWriteRequest { FeatureId = featureId, Scope = scope, Target = target, Outcome = outcome };
        if (actionId is not null)
        {
            request.ActionId = actionId;
        }

        await _client.RecordWriteAsync(request, _headers, DateTime.UtcNow.Add(_deadline), cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _channel.Dispose();
}
