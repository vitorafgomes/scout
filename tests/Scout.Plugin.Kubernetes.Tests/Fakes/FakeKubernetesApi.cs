using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Scout.Plugin.Kubernetes.Tests.Fakes;

/// <summary>
/// A Kubernetes API server on 127.0.0.1 that the real KubernetesClient talks to over HTTP.
/// Every request is recorded with its query before it is routed by path; an unknown path
/// answers 404.
/// </summary>
internal sealed class FakeKubernetesApi : IDisposable
{
    public const string DevToken = "dev-token-0123456789-secret";
    public const string StagingToken = "staging-token-0123456789-secret";
    public const string ExecEnvValue = "exec-env-value-secret";

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Kind, int Total, Func<int, string> Item)> _lists = new(StringComparer.Ordinal);
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-k8s-").FullName;

    public FakeKubernetesApi()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
        KubeconfigPath = Path.Combine(_directory, "config");
        File.WriteAllText(KubeconfigPath, Kubeconfig(Port, FreePort()));
    }

    public int Port { get; }

    public string KubeconfigPath { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public TimeSpan Delay { get; set; }

    public void Route(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        => _routes[path] = (status, json);

    /// <summary>A list of <paramref name="total"/> items that honours the limit query like a real API server.</summary>
    public void RouteList(string path, string kind, int total, Func<int, string> item)
        => _lists[path] = (kind, total, item);

    public void Dispose()
    {
        _listener.Close();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var pathAndQuery = Uri.UnescapeDataString(context.Request.Url!.PathAndQuery);
            Requests.Enqueue(new RecordedRequest(
                context.Request.HttpMethod, pathAndQuery, context.Request.Headers["Authorization"]));

            var path = context.Request.Url.AbsolutePath;
            var (status, body) = _lists.TryGetValue(path, out var list)
                ? (HttpStatusCode.OK, ListBody(list, context.Request.QueryString["limit"]))
                : _routes.TryGetValue(path, out var route)
                    ? route
                    : (HttpStatusCode.NotFound, """{"kind":"Status","apiVersion":"v1","code":404}""");
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static string ListBody((string Kind, int Total, Func<int, string> Item) list, string? limit)
    {
        var count = int.TryParse(limit, out var max) ? Math.Min(max, list.Total) : list.Total;
        var items = string.Join(",", Enumerable.Range(1, count).Select(list.Item));
        var metadata = count < list.Total
            ? $$"""{"continue":"next","remainingItemCount":{{list.Total - count}}}"""
            : "{}";
        return $$"""{"kind":"{{list.Kind}}","apiVersion":"v1","metadata":{{metadata}},"items":[{{items}}]}""";
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string Kubeconfig(int port, int closedPort) => $"""
        apiVersion: v1
        kind: Config
        current-context: dev
        clusters:
        - name: fake
          cluster:
            server: http://127.0.0.1:{port}
        - name: closed
          cluster:
            server: http://127.0.0.1:{closedPort}
        contexts:
        - name: dev
          context:
            cluster: fake
            user: dev-user
        - name: staging
          context:
            cluster: fake
            user: staging-user
        - name: prod
          context:
            cluster: fake
            user: dev-user
        - name: offline
          context:
            cluster: closed
            user: dev-user
        - name: sso
          context:
            cluster: fake
            user: exec-user
        - name: hung
          context:
            cluster: fake
            user: hung-user
        users:
        - name: dev-user
          user:
            token: {DevToken}
        - name: staging-user
          user:
            token: {StagingToken}
        - name: exec-user
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1
              command: "false"
              env:
              - name: SCOUT_EXEC_SECRET
                value: {ExecEnvValue}
        - name: hung-user
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1
              command: sleep
              args: ["40"]
        """;
}

internal sealed record RecordedRequest(string Method, string PathAndQuery, string? Authorization);
