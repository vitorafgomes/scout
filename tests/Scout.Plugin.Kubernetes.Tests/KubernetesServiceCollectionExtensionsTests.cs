using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubernetesServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ServiceCollection().AddKubernetesPlugin(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Path_And_Contexts_Register_The_Kubernetes_Plugin_Bound_From_Indexed_Keys()
    {
        using var provider = Build(new()
        {
            ["Scout:Plugins:Kubernetes:KubeconfigPath"] = "/home/me/.kube/config",
            ["Scout:Plugins:Kubernetes:Contexts:0"] = "dev",
            ["Scout:Plugins:Kubernetes:Contexts:1"] = "staging",
        });

        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();
        plugin.Name.ShouldBe("Kubernetes");
        provider.GetRequiredService<KubernetesPlugin>().ListContexts().ShouldBe("dev (default)\nstaging");
    }

    [Fact]
    public void No_Kubeconfig_Path_Registers_Nothing()
    {
        using var provider = Build(new() { ["Scout:Plugins:Kubernetes:Contexts:0"] = "dev" });

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }

    [Fact]
    public void No_Allowed_Context_Registers_Nothing()
    {
        using var provider = Build(new() { ["Scout:Plugins:Kubernetes:KubeconfigPath"] = "/home/me/.kube/config" });

        provider.GetServices<KernelPlugin>().ShouldBeEmpty();
    }
}
