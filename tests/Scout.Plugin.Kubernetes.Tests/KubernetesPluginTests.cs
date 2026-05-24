using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Core.Plugins;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubernetesPluginTests
{
    [Fact]
    public void Name_Is_Kubernetes()
    {
        new KubernetesPlugin().Name.ShouldBe("Kubernetes");
    }

    [Fact]
    public void FeatureId_Is_kubernetes()
    {
        new KubernetesPlugin().FeatureId.ShouldBe("kubernetes");
    }

    [Fact]
    public void Capabilities_Are_ReadOnly()
    {
        new KubernetesPlugin().Capabilities.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void Build_Returns_Six_KernelFunctions()
    {
        var readOps = Substitute.For<IKubernetesReadOps>();
        var options = Options.Create(new KubernetesPluginOptions());
        var functions = new KubernetesFunctions(readOps, NullLogger<KubernetesFunctions>.Instance, options);

        var services = new ServiceCollection();
        services.AddSingleton(functions);
        using var sp = services.BuildServiceProvider();

        var kernelPlugin = new KubernetesPlugin().Build(sp);

        kernelPlugin.Name.ShouldBe("Kubernetes");
        kernelPlugin.Select(f => f.Name).ShouldBe(new[]
        {
            "list_namespaces",
            "list_pods",
            "describe_pod",
            "get_pod_logs",
            "list_deployments",
            "list_events",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Can_Register_In_PluginRegistry_Because_ReadOnly()
    {
        var registry = new PluginRegistry();

        Action register = () => registry.Register(new KubernetesPlugin());

        register.ShouldNotThrow();
        registry.Plugins.ShouldHaveSingleItem().Name.ShouldBe("Kubernetes");
    }
}
