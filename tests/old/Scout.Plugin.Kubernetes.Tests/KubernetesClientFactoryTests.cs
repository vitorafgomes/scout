using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Core.Features;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubernetesClientFactoryTests
{
    private static IOptions<KubernetesPluginOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new KubernetesPluginOptions());

    [Fact]
    public async Task GetClient_Throws_When_No_Kubeconfig_Stored()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/kubernetes/kubeconfig", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var factory = new KubernetesClientFactory(
            store, Options(), NullLogger<KubernetesClientFactory>.Instance,
            (_, _) => Substitute.For<IKubernetes>());

        await Should.ThrowAsync<InvalidOperationException>(
            () => factory.GetClientAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetClient_Caches_Per_Credential_Snapshot()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/kubernetes/kubeconfig", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("yaml-v1"));
        store.GetAsync("scout/kubernetes/context", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var builds = 0;
        var factory = new KubernetesClientFactory(
            store, Options(), NullLogger<KubernetesClientFactory>.Instance,
            (_, _) =>
            {
                builds++;
                return Substitute.For<IKubernetes>();
            });

        var c1 = await factory.GetClientAsync(CancellationToken.None);
        var c2 = await factory.GetClientAsync(CancellationToken.None);

        c1.ShouldBeSameAs(c2);
        builds.ShouldBe(1);
    }

    [Fact]
    public async Task GetClient_Rebuilds_When_Credentials_Change()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/kubernetes/kubeconfig", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("yaml-v1"), Task.FromResult<string?>("yaml-v2"));
        store.GetAsync("scout/kubernetes/context", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var builds = 0;
        var factory = new KubernetesClientFactory(
            store, Options(), NullLogger<KubernetesClientFactory>.Instance,
            (_, _) =>
            {
                builds++;
                return Substitute.For<IKubernetes>();
            });

        _ = await factory.GetClientAsync(CancellationToken.None);
        _ = await factory.GetClientAsync(CancellationToken.None);

        builds.ShouldBe(2);
    }

    [Fact]
    public async Task Invalidate_Forces_Rebuild()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/kubernetes/kubeconfig", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("yaml-v1"));
        store.GetAsync("scout/kubernetes/context", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var builds = 0;
        var factory = new KubernetesClientFactory(
            store, Options(), NullLogger<KubernetesClientFactory>.Instance,
            (_, _) =>
            {
                builds++;
                return Substitute.For<IKubernetes>();
            });

        _ = await factory.GetClientAsync(CancellationToken.None);
        factory.Invalidate();
        _ = await factory.GetClientAsync(CancellationToken.None);

        builds.ShouldBe(2);
    }
}
