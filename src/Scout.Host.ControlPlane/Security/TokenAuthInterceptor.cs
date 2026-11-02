using Grpc.Core;
using Grpc.Core.Interceptors;
using Scout.ControlPlane.Contracts;
using ProtoServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;

namespace Scout.Host.ControlPlane.Security;

/// <summary>
/// Validates the caller's token on every unary and streaming call and allows
/// only the services of its role: the desktop token reaches Features, Providers
/// and Models, the worker token reaches WorkerGate, and everything else fails
/// with <see cref="StatusCode.PermissionDenied"/>. The token is read from the
/// <c>authorization</c> metadata header (with an optional <c>Bearer </c> prefix)
/// or the <c>x-scout-token</c> header. Absent or mismatched tokens fail with
/// <see cref="StatusCode.Unauthenticated"/>.
/// </summary>
public sealed class TokenAuthInterceptor : Interceptor
{
    private const string AuthorizationHeader = "authorization";
    private const string ScoutTokenHeader = "x-scout-token";
    private const string BearerPrefix = "Bearer ";

    private static readonly string[] DesktopServices =
    [
        Prefix(Features.Descriptor),
        Prefix(Providers.Descriptor),
        Prefix(Models.Descriptor),
    ];

    private static readonly string[] WorkerServices = [Prefix(WorkerGate.Descriptor)];

    private readonly CallerTokens _tokens;

    public TokenAuthInterceptor(CallerTokens tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        var presented = Extract(context.RequestHeaders);
        string[] allowed;
        if (_tokens.Desktop.Matches(presented))
        {
            allowed = DesktopServices;
        }
        else if (_tokens.Worker?.Matches(presented) == true)
        {
            allowed = WorkerServices;
        }
        else
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Missing or invalid session token."));
        }

        if (!allowed.Any(prefix => context.Method.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                $"This caller may not call {context.Method}."));
        }
    }

    private static string Prefix(ProtoServiceDescriptor service) => $"/{service.FullName}/";

    private static string? Extract(Metadata headers)
    {
        foreach (var entry in headers)
        {
            if (entry.IsBinary)
            {
                continue;
            }

            if (string.Equals(entry.Key, ScoutTokenHeader, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }

            if (string.Equals(entry.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase))
            {
                var value = entry.Value;
                return value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                    ? value[BearerPrefix.Length..]
                    : value;
            }
        }

        return null;
    }
}
