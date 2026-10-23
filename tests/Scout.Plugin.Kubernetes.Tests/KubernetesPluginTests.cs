using System.Globalization;
using System.Net;
using System.Web;
using k8s;
using Microsoft.SemanticKernel;
using Scout.Plugin.Kubernetes.Tests.Fakes;

namespace Scout.Plugin.Kubernetes.Tests;

/// <summary>
/// Drives the real KubernetesClient against <see cref="FakeKubernetesApi"/> through a temp
/// kubeconfig, so every assertion is about real HTTP requests.
/// </summary>
public sealed class KubernetesPluginTests : IDisposable
{
    private const string PodList = """
        {"kind":"PodList","apiVersion":"v1","metadata":{},"items":[
          {"metadata":{"name":"web-1","namespace":"shop"},
           "spec":{"containers":[{"name":"app","image":"shop:1"}]},
           "status":{"phase":"Running","containerStatuses":[
             {"name":"app","image":"shop:1","imageID":"","ready":false,"restartCount":7,
              "state":{"waiting":{"reason":"CrashLoopBackOff"}},
              "lastState":{"terminated":{"exitCode":1,"reason":"Error"}}}]}},
          {"metadata":{"name":"api-1","namespace":"shop"},
           "spec":{"containers":[{"name":"api","image":"api:2"}]},
           "status":{"phase":"Running","containerStatuses":[
             {"name":"api","image":"api:2","imageID":"","ready":true,"restartCount":0,
              "state":{"running":{}}}]}}]}
        """;

    private readonly FakeKubernetesApi _api = new();
    private readonly KubernetesPlugin _plugin;

    public KubernetesPluginTests()
    {
        _plugin = new KubernetesPlugin(new KubernetesOptions
        {
            KubeconfigPath = _api.KubeconfigPath,
            Contexts = ["dev", "staging", "offline", "sso"],
        });
    }

    public void Dispose()
    {
        _plugin.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task List_Pods_Returns_The_Clusters_Pods_From_The_Kubeconfig_On_This_Computer()
    {
        _api.Route("/api/v1/namespaces/shop/pods", PodList);

        var result = await _plugin.ListPodsAsync("shop");

        result.ShouldBe(
            "shop/web-1 Running, ready 0/1, restarts 7, CrashLoopBackOff\n" +
            "shop/api-1 Running, ready 1/1, restarts 0");
    }

    [Fact]
    public async Task Omitted_Context_Uses_The_First_Allowed_Context()
    {
        _api.Route("/api/v1/pods", PodList);

        await _plugin.ListPodsAsync();

        _api.Requests.ShouldHaveSingleItem().Authorization.ShouldBe($"Bearer {FakeKubernetesApi.DevToken}");
    }

    [Fact]
    public async Task A_Named_Allowed_Context_Uses_That_Contexts_Credentials()
    {
        _api.Route("/api/v1/pods", PodList);

        await _plugin.ListPodsAsync(context: "staging");

        _api.Requests.ShouldHaveSingleItem().Authorization.ShouldBe($"Bearer {FakeKubernetesApi.StagingToken}");
    }

    [Fact]
    public async Task A_Context_The_Owner_Did_Not_Approve_Is_Refused_And_No_Request_Is_Sent()
    {
        _api.Route("/api/v1/pods", PodList);

        var result = await _plugin.ListPodsAsync(context: "prod");

        result.ShouldBe("Context prod is not one Scout may read. Allowed contexts: dev, staging, offline, sso.");
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Forbidden_Says_The_Credentials_Cannot_Read_And_Never_Echoes_The_Answer_Or_Token()
    {
        _api.Route("/api/v1/pods", """{"kind":"Status","message":"MARKER-cluster-body"}""", HttpStatusCode.Forbidden);

        var result = await _plugin.ListPodsAsync();

        result.ShouldBe("The credentials for context dev cannot read pods.");
        result.ShouldNotContain("MARKER");
        result.ShouldNotContain(FakeKubernetesApi.DevToken);
    }

    [Fact]
    public async Task Unauthorized_Says_The_Credentials_Cannot_Read()
    {
        _api.Route("/api/v1/namespaces", "{}", HttpStatusCode.Unauthorized);

        var result = await _plugin.ListNamespacesAsync();

        result.ShouldBe("The credentials for context dev cannot read namespaces.");
    }

    [Fact]
    public async Task An_Unreachable_Cluster_Says_So()
    {
        var result = await _plugin.ListPodsAsync(context: "offline");

        result.ShouldBe("Could not reach the cluster for context offline.");
    }

    [Fact]
    public async Task A_Failing_Exec_Credential_Command_Says_The_Token_Command_Failed_And_Sends_Nothing()
    {
        _api.Route("/api/v1/pods", PodList);

        var result = await _plugin.ListPodsAsync(context: "sso");

        result.ShouldBe("The command that gets the token for context sso failed.");
        result.ShouldNotContain(FakeKubernetesApi.ExecEnvValue);
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Missing_Kubeconfig_Says_The_Configuration_Could_Not_Be_Read()
    {
        using var plugin = new KubernetesPlugin(new KubernetesOptions
        {
            KubeconfigPath = Path.Combine(Path.GetTempPath(), $"scout-missing-{Guid.NewGuid():N}"),
            Contexts = ["dev"],
        });

        var result = await plugin.ListPodsAsync();

        result.ShouldBe("The Kubernetes configuration for context dev could not be read.");
    }

    [Fact]
    public async Task List_Namespaces_Lists_Each_With_Its_Phase()
    {
        _api.Route("/api/v1/namespaces", """
            {"kind":"NamespaceList","apiVersion":"v1","metadata":{},"items":[
              {"metadata":{"name":"default"},"status":{"phase":"Active"}},
              {"metadata":{"name":"old"},"status":{"phase":"Terminating"}}]}
            """);

        var result = await _plugin.ListNamespacesAsync();

        result.ShouldBe("default (Active)\nold (Terminating)");
    }

    [Fact]
    public async Task List_Deployments_Shows_Ready_Over_Desired()
    {
        _api.Route("/apis/apps/v1/namespaces/shop/deployments", """
            {"kind":"DeploymentList","apiVersion":"apps/v1","metadata":{},"items":[
              {"metadata":{"name":"web","namespace":"shop"},"spec":{"replicas":3,"selector":{},"template":{}},
               "status":{"readyReplicas":1}}]}
            """);

        var result = await _plugin.ListDeploymentsAsync("shop");

        result.ShouldBe("shop/web ready 1/3");
    }

    [Fact]
    public async Task Describe_Pod_Shows_Container_Statuses_And_The_Pods_Events_Newest_First()
    {
        _api.Route("/api/v1/namespaces/shop/pods/web-1", SinglePod());
        _api.Route(
            "/api/v1/namespaces/shop/events",
            EventList(
                Event("Normal", "Pulled", "Container image pulled", minutesAgo: 30),
                Event("Warning", "BackOff", "Back-off restarting failed container", minutesAgo: 1)));

        var result = await _plugin.DescribePodAsync("shop", "web-1");

        var lines = result.Split('\n');
        lines[0].ShouldBe("shop/web-1 Running, ready 0/1, restarts 7, CrashLoopBackOff");
        lines[1].ShouldBe("Container app (shop:1): Waiting (CrashLoopBackOff), ready no, restarts 7, last terminated Error (exit 1)");
        lines[2].ShouldBe("Events, newest first:");
        lines[3].ShouldEndWith("Warning shop/Pod/web-1 BackOff: Back-off restarting failed container");
        lines[4].ShouldEndWith("Normal shop/Pod/web-1 Pulled: Container image pulled");
        _api.Requests.ShouldContain(r =>
            r.PathAndQuery.StartsWith("/api/v1/namespaces/shop/events?", StringComparison.Ordinal)
            && r.PathAndQuery.Contains("fieldSelector=involvedObject.kind=Pod,involvedObject.name=web-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Describe_Pod_Keeps_Only_The_Last_20_Events()
    {
        _api.Route("/api/v1/namespaces/shop/pods/web-1", SinglePod());
        _api.Route(
            "/api/v1/namespaces/shop/events",
            EventList(Enumerable.Range(1, 25).Select(i => Event("Normal", $"R{i}", "m", minutesAgo: i)).ToArray()));

        var result = await _plugin.DescribePodAsync("shop", "web-1");

        result.Split('\n').Count(l => l.Contains(" Normal ", StringComparison.Ordinal)).ShouldBe(20);
        result.ShouldContain(" R1: m");
        result.ShouldNotContain(" R21: m");
    }

    [Fact]
    public async Task List_Events_Puts_Warnings_First_And_Drops_Events_Older_Than_The_Window()
    {
        _api.Route("/api/v1/events", EventList(
            Event("Normal", "Scheduled", "fresh normal", minutesAgo: 1),
            Event("Warning", "Failed", "older warning", minutesAgo: 8),
            Event("Warning", "BackOff", "newer warning", minutesAgo: 2),
            Event("Warning", "Evicted", "outside the window", minutesAgo: 30)));

        var result = await _plugin.ListEventsAsync();

        var lines = result.Split('\n');
        lines.Length.ShouldBe(3);
        lines[0].ShouldEndWith("BackOff: newer warning");
        lines[1].ShouldEndWith("Failed: older warning");
        lines[2].ShouldEndWith("Scheduled: fresh normal");
    }

    [Fact]
    public void List_Contexts_Lists_Only_The_Allowed_Contexts_With_The_Default_First()
    {
        _plugin.ListContexts().ShouldBe("dev (default)\nstaging\noffline\nsso");
    }

    [Fact]
    public void Exposes_Exactly_The_Read_Functions_And_Nothing_Else()
    {
        var plugin = KernelPluginFactory.CreateFromObject(_plugin, "Kubernetes");

        plugin.Select(f => f.Name).Order(StringComparer.Ordinal).ShouldBe(
        [
            "describe_pod", "list_contexts", "list_deployments", "list_events", "list_namespaces", "list_pods",
        ]);
    }

    [Fact]
    public async Task Every_Function_Sends_Only_Get_Requests()
    {
        var kernel = new Kernel();
        var plugin = kernel.ImportPluginFromObject(_plugin, "Kubernetes");

        foreach (var function in plugin)
        {
            await kernel.InvokeAsync(function, new KernelArguments
            {
                ["namespace"] = "shop",
                ["name"] = "web-1",
            });
            await kernel.InvokeAsync(function, new KernelArguments { ["name"] = "web-1", ["namespace"] = "shop", ["context"] = "staging" });
        }

        _api.Requests.Count.ShouldBeGreaterThanOrEqualTo(10);
        _api.Requests.ShouldAllBe(r => r.Method == "GET");
    }

    [Fact]
    public async Task List_Pods_Asks_The_Server_For_100_And_Says_How_Many_More()
    {
        _api.RouteList("/api/v1/pods", "PodList", 120, i =>
            $$$"""{"metadata":{"name":"p-{{{i}}}","namespace":"big"},"status":{"phase":"Running"}}""");

        var lines = (await _plugin.ListPodsAsync()).Split('\n');

        lines.Length.ShouldBe(101);
        lines[99].ShouldStartWith("big/p-100 ");
        lines[100].ShouldBe("...and 20 more; ask about one namespace to narrow it.");
        _api.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldContain("limit=100");
    }

    [Fact]
    public async Task A_Continue_Token_Without_A_Count_Says_There_Are_More()
    {
        _api.Route("/api/v1/pods", """
            {"kind":"PodList","apiVersion":"v1","metadata":{"continue":"next"},"items":[
              {"metadata":{"name":"p-1","namespace":"big"},"status":{"phase":"Running"}}]}
            """);

        var lines = (await _plugin.ListPodsAsync()).Split('\n');

        lines.ShouldBe(["big/p-1 Running, ready 0/0, restarts 0", "...and more; ask about one namespace to narrow it."]);
    }

    [Fact]
    public async Task List_Deployments_And_List_Namespaces_Ask_The_Server_For_At_Most_100()
    {
        _api.RouteList("/apis/apps/v1/namespaces/shop/deployments", "DeploymentList", 3, i =>
            $$$$"""{"metadata":{"name":"d-{{{{i}}}}","namespace":"shop"},"spec":{"replicas":1,"selector":{},"template":{}}}""");
        _api.RouteList("/api/v1/namespaces", "NamespaceList", 3, i => $$$$"""{"metadata":{"name":"n-{{{{i}}}}"}}""");

        await _plugin.ListDeploymentsAsync("shop");
        await _plugin.ListNamespacesAsync();

        _api.Requests
            .Select(r => HttpUtility.ParseQueryString(new Uri(new Uri("http://cluster"), r.PathAndQuery).Query)["limit"])
            .ShouldBe(["100", "100"]);
    }

    [Theory]
    [InlineData("../secrets/x")]
    [InlineData("x/services/y:80/proxy/z?")]
    [InlineData("default%2Fsecrets")]
    [InlineData("a,involvedObject.kind=Secret")]
    [InlineData("Web-1")]
    [InlineData("   ")]
    [InlineData("web-1\n")]
    [InlineData("a..b")]
    public async Task Describe_Pod_Refuses_A_Pod_Name_That_Is_Not_A_Kubernetes_Name_And_Sends_Nothing(string name)
    {
        _api.Route("/api/v1/namespaces/shop/pods/web-1", SinglePod());

        var result = await _plugin.DescribePodAsync("shop", name);

        result.ShouldBe($"'{name}' is not a valid Kubernetes pod name.");
        _api.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../secrets/x")]
    [InlineData("x/services/y:80/proxy/z?")]
    [InlineData("default%2Fsecrets")]
    [InlineData("a,involvedObject.kind=Secret")]
    [InlineData("Shop")]
    [InlineData("shop\n")]
    public async Task Namespaced_Functions_Refuse_A_Namespace_That_Is_Not_A_Kubernetes_Name_And_Send_Nothing(string @namespace)
    {
        var results = new[]
        {
            await _plugin.ListPodsAsync(@namespace),
            await _plugin.ListDeploymentsAsync(@namespace),
            await _plugin.ListEventsAsync(@namespace),
            await _plugin.DescribePodAsync(@namespace, "web-1"),
        };

        results.ShouldAllBe(r => r == $"'{@namespace}' is not a valid Kubernetes namespace.");
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Describe_Pod_Refuses_A_Blank_Namespace_And_Sends_Nothing()
    {
        var result = await _plugin.DescribePodAsync("  ", "web-1");

        result.ShouldBe("'  ' is not a valid Kubernetes namespace.");
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Refused_Value_Is_Quoted_At_Most_63_Characters()
    {
        var result = await _plugin.DescribePodAsync("shop", new string('a', 300));

        result.ShouldBe($"'{new string('a', 63)}' is not a valid Kubernetes pod name.");
    }

    [Fact]
    public async Task List_Events_With_A_Huge_Window_Uses_The_Last_Day()
    {
        _api.Route("/api/v1/events", EventList(
            Event("Normal", "Scheduled", "an hour ago", minutesAgo: 60),
            Event("Warning", "Evicted", "two days ago", minutesAgo: 2880)));

        var result = await _plugin.ListEventsAsync(sinceMinutes: int.MaxValue);

        result.Split('\n').ShouldHaveSingleItem().ShouldEndWith("Scheduled: an hour ago");
    }

    [Fact]
    public async Task A_Long_Event_Message_Is_Cut_At_200_Characters()
    {
        _api.Route("/api/v1/events", EventList(Event("Warning", "Failed", new string('m', 300), minutesAgo: 1)));

        var result = await _plugin.ListEventsAsync();

        result.ShouldEndWith($"Failed: {new string('m', 200)}...");
    }

    [Fact]
    public async Task A_Caller_Cancellation_Propagates_Instead_Of_Answering_With_A_Timeout()
    {
        _api.Route("/api/v1/pods", PodList);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _plugin.ListPodsAsync(ct: cancelled.Token));
    }

    [Fact]
    public async Task A_Cluster_That_Does_Not_Answer_In_Time_Says_So()
    {
        _api.Route("/api/v1/pods", PodList);
        _api.Delay = TimeSpan.FromSeconds(2);
        using var plugin = new KubernetesPlugin(
            new KubernetesOptions { KubeconfigPath = _api.KubeconfigPath, Contexts = ["dev"] },
            context =>
            {
                var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(_api.KubeconfigPath, context);
                config.HttpClientTimeout = TimeSpan.FromMilliseconds(200);
                return new k8s.Kubernetes(config);
            });

        var result = await plugin.ListPodsAsync();

        result.ShouldBe("The cluster for context dev did not answer within 15 seconds.");
    }

    [Fact]
    public async Task A_Hung_Credential_Command_Fails_After_15_Seconds_Not_2_Minutes()
    {
        using var plugin = new KubernetesPlugin(new KubernetesOptions { KubeconfigPath = _api.KubeconfigPath, Contexts = ["hung"] });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await plugin.ListPodsAsync();

        result.ShouldBe("The command that gets the token for context hung failed.");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        _api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_First_Calls_On_A_Context_Build_One_Client()
    {
        _api.Route("/api/v1/pods", PodList);
        var builds = 0;
        var firstBuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstBuild = new ManualResetEventSlim();
        using var plugin = new KubernetesPlugin(
            new KubernetesOptions { KubeconfigPath = _api.KubeconfigPath, Contexts = ["dev"] },
            context =>
            {
                if (Interlocked.Increment(ref builds) == 1)
                {
                    firstBuildEntered.SetResult();
                    releaseFirstBuild.Wait(TimeSpan.FromSeconds(10));
                }

                return new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(_api.KubeconfigPath, context));
            });

        var first = Task.Run(() => plugin.ListPodsAsync());
        await firstBuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<string>? second = null;
        var secondCaller = new Thread(() => second = plugin.ListPodsAsync());
        secondCaller.Start();
        var secondEnteredWhileFirstBuilds = SpinWait.SpinUntil(
            () => (secondCaller.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
            TimeSpan.FromSeconds(10));
        releaseFirstBuild.Set();
        secondCaller.Join();
        var results = await Task.WhenAll(first, second!);

        secondEnteredWhileFirstBuilds.ShouldBeTrue();
        builds.ShouldBe(1);
        results.ShouldAllBe(r => r.StartsWith("shop/web-1 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Failed_Client_Build_Is_Retried_On_The_Next_Call()
    {
        var path = Path.Combine(Path.GetDirectoryName(_api.KubeconfigPath)!, "later");
        _api.Route("/api/v1/pods", PodList);
        using var plugin = new KubernetesPlugin(new KubernetesOptions { KubeconfigPath = path, Contexts = ["dev"] });

        var before = await plugin.ListPodsAsync();
        File.Copy(_api.KubeconfigPath, path);
        var after = await plugin.ListPodsAsync();

        before.ShouldBe("The Kubernetes configuration for context dev could not be read.");
        after.ShouldStartWith("shop/web-1 ");
    }

    [Fact]
    public async Task An_Http_Failure_Without_A_Response_Says_The_Cluster_Could_Not_Be_Read()
    {
        using var plugin = new KubernetesPlugin(
            new KubernetesOptions { KubeconfigPath = _api.KubeconfigPath, Contexts = ["dev"] },
            context => new k8s.Kubernetes(
                KubernetesClientConfiguration.BuildConfigFromConfigFile(_api.KubeconfigPath, context),
                new ThrowsWithoutResponse()));

        var result = await plugin.ListPodsAsync();

        result.ShouldBe("The cluster for context dev could not be read.");
    }

    private sealed class ThrowsWithoutResponse : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new k8s.Autorest.HttpOperationException("no response");
    }

    private static string SinglePod() => """
        {"kind":"Pod","apiVersion":"v1",
         "metadata":{"name":"web-1","namespace":"shop"},
         "spec":{"containers":[{"name":"app","image":"shop:1"}]},
         "status":{"phase":"Running","containerStatuses":[
           {"name":"app","image":"shop:1","imageID":"","ready":false,"restartCount":7,
            "state":{"waiting":{"reason":"CrashLoopBackOff"}},
            "lastState":{"terminated":{"exitCode":1,"reason":"Error"}}}]}}
        """;

    private static string EventList(params string[] events)
        => $$"""{"kind":"EventList","apiVersion":"v1","metadata":{},"items":[{{string.Join(",", events)}}]}""";

    private static string Event(string type, string reason, string message, int minutesAgo)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $$"""
            {"metadata":{"name":"e-{{reason}}","namespace":"shop"},
             "involvedObject":{"kind":"Pod","name":"web-1","namespace":"shop"},
             "type":"{{type}}","reason":"{{reason}}","message":"{{message}}","lastTimestamp":"{{at}}"}
            """;
    }
}
