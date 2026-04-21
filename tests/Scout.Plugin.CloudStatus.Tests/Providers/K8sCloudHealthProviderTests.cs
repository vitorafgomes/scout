using System.Net;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;
using Scout.Plugin.CloudStatus.Providers.Kubernetes;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class K8sCloudHealthProviderTests
{
    private const string Cluster = "prod-eks";
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static K8sCloudHealthProvider Sut(IK8sHealthSource source) =>
        new(
            Options.Create(new K8sCloudHealthOptions { ClusterName = Cluster }),
            source,
            NullLogger<K8sCloudHealthProvider>.Instance,
            new FixedTimeProvider(FixedTime));

    private static HttpOperationException HttpOp(HttpStatusCode status) =>
        new($"HTTP {(int)status}")
        {
            Response = new HttpResponseMessageWrapper(
                new HttpResponseMessage(status), string.Empty),
        };

    [Fact]
    public async Task Returns_Healthy_When_No_Issues()
    {
        var report = await Sut(new StubSource(Array.Empty<K8sHealthIssue>()))
            .GetHealthAsync(CancellationToken.None);

        report.Provider.Should().Be("Kubernetes");
        report.Scope.Should().Be(Cluster);
        report.Overall.Should().Be(HealthLevel.Healthy);
        report.Services.Should().BeEmpty();
        report.CheckedAt.Should().Be(FixedTime);
    }

    [Fact]
    public async Task Critical_Issue_Maps_To_Unhealthy()
    {
        var source = new StubSource([
            new K8sHealthIssue("Node", "ip-10-0-1-2", "(cluster)", "NodeNotReady", "critical"),
            new K8sHealthIssue("Pod", "api-abc", "prod", "CrashLoopBackOff", "critical"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unhealthy);
        report.Services.Should().HaveCount(2);
        report.Services.Should().Contain(s =>
            s.Service == "Pod" && s.Region == "prod" && s.Level == HealthLevel.Unhealthy);
    }

    [Fact]
    public async Task Warning_Only_Maps_To_Degraded()
    {
        var source = new StubSource([
            new K8sHealthIssue("Pod", "worker-xyz", "prod", "Pending", "warning"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task Unknown_Severity_Treated_As_Degraded()
    {
        var source = new StubSource([
            new K8sHealthIssue("Pod", "p", "default", "WeirdReason", "info"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task Issue_Name_And_Reason_Are_Surfaced_In_ServiceStatus_Message()
    {
        var source = new StubSource([
            new K8sHealthIssue("Pod", "worker-xyz", "prod", "ImagePullBackOff", "critical"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Services.Should().ContainSingle()
              .Which.Message.Should().Contain("worker-xyz")
              .And.Contain("ImagePullBackOff");
    }

    [Fact]
    public async Task Forbidden_Response_Maps_To_Unknown_With_RBAC_Hint()
    {
        var source = new ThrowingSource(HttpOp(HttpStatusCode.Forbidden));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("RBAC");
    }

    [Fact]
    public async Task Unauthorized_Response_Maps_To_Unknown_With_RBAC_Hint()
    {
        var source = new ThrowingSource(HttpOp(HttpStatusCode.Unauthorized));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("RBAC");
    }

    [Fact]
    public async Task Other_HttpOperationException_Maps_To_Unknown_With_Status()
    {
        var source = new ThrowingSource(HttpOp(HttpStatusCode.InternalServerError));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("HTTP 500");
    }

    [Fact]
    public async Task HttpRequestException_Maps_To_Unreachable_Unknown()
    {
        var source = new ThrowingSource(new HttpRequestException("no route to host"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("unreachable");
    }

    [Fact]
    public async Task Unexpected_Exception_Maps_To_Unknown_With_Message()
    {
        var source = new ThrowingSource(new InvalidOperationException("boom"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Propagates_Caller_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Sut(new CancellableSource()).GetHealthAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_Throws_When_ClusterName_Empty()
    {
        Action create = () => new K8sCloudHealthProvider(
            Options.Create(new K8sCloudHealthOptions { ClusterName = "" }),
            new StubSource(Array.Empty<K8sHealthIssue>()),
            NullLogger<K8sCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*ClusterName*");
    }

    [Fact]
    public void Ctor_Throws_When_Source_Null()
    {
        Action create = () => new K8sCloudHealthProvider(
            Options.Create(new K8sCloudHealthOptions { ClusterName = Cluster }),
            source: null!,
            NullLogger<K8sCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubSource(IReadOnlyList<K8sHealthIssue> issues) : IK8sHealthSource
    {
        public Task<IReadOnlyList<K8sHealthIssue>> GetActiveIssuesAsync(CancellationToken ct) =>
            Task.FromResult(issues);
    }

    private sealed class ThrowingSource(Exception exception) : IK8sHealthSource
    {
        public Task<IReadOnlyList<K8sHealthIssue>> GetActiveIssuesAsync(CancellationToken ct) =>
            throw exception;
    }

    private sealed class CancellableSource : IK8sHealthSource
    {
        public Task<IReadOnlyList<K8sHealthIssue>> GetActiveIssuesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<K8sHealthIssue>>(Array.Empty<K8sHealthIssue>());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
