using System.Text;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubernetesFunctionsTests
{
    private static KubernetesFunctions Build(
        IKubernetesReadOps readOps,
        KubernetesPluginOptions? opts = null)
    {
        return new KubernetesFunctions(
            readOps,
            NullLogger<KubernetesFunctions>.Instance,
            Options.Create(opts ?? new KubernetesPluginOptions()));
    }

    [Fact]
    public async Task ListNamespaces_Maps_Items()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ListNamespacesAsync(Arg.Any<CancellationToken>())
            .Returns(new V1NamespaceList
            {
                Items = new List<V1Namespace>
                {
                    new()
                    {
                        Metadata = new V1ObjectMeta { Name = "default", CreationTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                        Status = new V1NamespaceStatus { Phase = "Active" },
                    },
                },
            });

        var result = await Build(readOps).ListNamespacesAsync(CancellationToken.None);

        var only = result.ShouldHaveSingleItem();
        only.Name.ShouldBe("default");
        only.Status.ShouldBe("Active");
        only.Citation.Label.ShouldBe("kubectl get ns default");
    }

    [Fact]
    public async Task ListPods_NoNamespace_Calls_AllNamespaces()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ListPodsForAllNamespacesAsync(Arg.Any<CancellationToken>())
            .Returns(new V1PodList { Items = new List<V1Pod> { MakePod("ns1", "pod1") } });

        var result = await Build(readOps).ListPodsAsync(null, CancellationToken.None);

        await readOps.Received(1).ListPodsForAllNamespacesAsync(Arg.Any<CancellationToken>());
        await readOps.DidNotReceive().ListPodsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.ShouldHaveSingleItem().Name.ShouldBe("pod1");
    }

    [Fact]
    public async Task ListPods_WithNamespace_Calls_Namespaced()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ListPodsAsync("kube-system", Arg.Any<CancellationToken>())
            .Returns(new V1PodList { Items = new List<V1Pod> { MakePod("kube-system", "coredns") } });

        var result = await Build(readOps).ListPodsAsync("kube-system", CancellationToken.None);

        await readOps.Received(1).ListPodsAsync("kube-system", Arg.Any<CancellationToken>());
        result.ShouldHaveSingleItem().Namespace.ShouldBe("kube-system");
    }

    [Fact]
    public async Task DescribePod_Filters_Events_To_Pod()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ReadPodAsync("pod1", "ns1", Arg.Any<CancellationToken>())
            .Returns(MakePod("ns1", "pod1"));
        readOps.ListEventsAsync("ns1", Arg.Any<CancellationToken>())
            .Returns(new Corev1EventList
            {
                Items = new List<Corev1Event>
                {
                    new()
                    {
                        Metadata = new V1ObjectMeta { NamespaceProperty = "ns1" },
                        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "pod1" },
                        Type = "Warning", Reason = "Backoff", Message = "x",
                        Count = 1, LastTimestamp = DateTime.UtcNow,
                    },
                    new()
                    {
                        Metadata = new V1ObjectMeta { NamespaceProperty = "ns1" },
                        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "other" },
                        Type = "Normal", Reason = "Pulled", Message = "y",
                        Count = 1, LastTimestamp = DateTime.UtcNow,
                    },
                    new()
                    {
                        Metadata = new V1ObjectMeta { NamespaceProperty = "ns1" },
                        InvolvedObject = new V1ObjectReference { Kind = "Node", Name = "pod1" },
                        Type = "Normal", Reason = "X", Message = "z",
                        Count = 1, LastTimestamp = DateTime.UtcNow,
                    },
                },
            });

        var result = await Build(readOps).DescribePodAsync("ns1", "pod1", CancellationToken.None);

        result.Summary.Name.ShouldBe("pod1");
        result.RecentEvents.ShouldHaveSingleItem().Reason.ShouldBe("Backoff");
        result.Citation.Label.ShouldBe("kubectl describe pod -n ns1 pod1");
    }

    [Fact]
    public async Task GetPodLogs_Clamps_TailLines_And_PrefixesBanner()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ReadPodLogsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("line1\nline2"))));

        var options = new KubernetesPluginOptions { MaxLogTailLines = 50 };

        var result = await Build(readOps, options).GetPodLogsAsync(
            @namespace: "ns1",
            pod: "pod1",
            container: "main",
            tailLines: 9999,
            sinceSeconds: null,
            cancellationToken: CancellationToken.None);

        result.Truncated.ShouldBeTrue();
        result.Logs.ShouldStartWith("[scout: tailLines clamped from 9999 to 50]");
        result.Container.ShouldBe("main");

        await readOps.Received(1).ReadPodLogsAsync(
            "pod1", "ns1", "main", 50, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPodLogs_RespectsRequested_WhenWithinBounds()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ReadPodLogsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hello"))));

        var result = await Build(readOps).GetPodLogsAsync(
            @namespace: "ns1",
            pod: "pod1",
            container: null,
            tailLines: 100,
            sinceSeconds: 60,
            cancellationToken: CancellationToken.None);

        result.Truncated.ShouldBeFalse();
        result.Logs.ShouldBe("hello");
        await readOps.Received(1).ReadPodLogsAsync(
            "pod1", "ns1", null, 100, 60, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListDeployments_NoNamespace_CallsAll()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ListDeploymentsForAllNamespacesAsync(Arg.Any<CancellationToken>())
            .Returns(new V1DeploymentList
            {
                Items = new List<V1Deployment>
                {
                    new()
                    {
                        Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "ns1" },
                        Spec = new V1DeploymentSpec { Replicas = 3, Strategy = new V1DeploymentStrategy { Type = "RollingUpdate" } },
                        Status = new V1DeploymentStatus { ReadyReplicas = 2, UpdatedReplicas = 3, AvailableReplicas = 2 },
                    },
                },
            });

        var result = await Build(readOps).ListDeploymentsAsync(null, CancellationToken.None);

        var only = result.ShouldHaveSingleItem();
        only.Replicas.ShouldBe(3);
        only.ReadyReplicas.ShouldBe(2);
        only.Strategy.ShouldBe("RollingUpdate");
        only.Citation.Label.ShouldBe("kubectl describe deployment -n ns1 api");
    }

    [Fact]
    public async Task ListEvents_FiltersBySinceSeconds()
    {
        var now = DateTime.UtcNow;
        var readOps = Substitute.For<IKubernetesReadOps>();
        readOps.ListEventsForAllNamespacesAsync(Arg.Any<CancellationToken>())
            .Returns(new Corev1EventList
            {
                Items = new List<Corev1Event>
                {
                    new()
                    {
                        Metadata = new V1ObjectMeta { NamespaceProperty = "ns1" },
                        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "fresh" },
                        Type = "Normal", Reason = "Started", Message = "",
                        Count = 1, LastTimestamp = now.AddSeconds(-30),
                    },
                    new()
                    {
                        Metadata = new V1ObjectMeta { NamespaceProperty = "ns1" },
                        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "old" },
                        Type = "Normal", Reason = "Old", Message = "",
                        Count = 1, LastTimestamp = now.AddHours(-2),
                    },
                },
            });

        var result = await Build(readOps).ListEventsAsync(null, 600, CancellationToken.None);

        result.ShouldHaveSingleItem().ObjectName.ShouldBe("fresh");
    }

    private static V1Pod MakePod(string ns, string name)
        => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                CreationTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Spec = new V1PodSpec
            {
                NodeName = "node-1",
                Containers = new List<V1Container> { new() { Name = "main", Image = "nginx:1" } },
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                StartTime = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc),
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new()
                    {
                        Name = "main",
                        Image = "nginx:1",
                        Ready = true,
                        RestartCount = 0,
                        State = new V1ContainerState { Running = new V1ContainerStateRunning() },
                    },
                },
            },
        };
}
