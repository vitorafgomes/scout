using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Features;
using Scout.Llm.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class WriteDeclarationCatalogTests
{
    [Fact]
    public void Every_Write_The_Host_Declares_Names_A_Write_Row_Of_Its_Feature_In_The_Catalog_Exactly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real",
                ["Scout:Plugins:Kubernetes:KubeconfigPath"] = "/nonexistent/scout-test/kubeconfig",
                ["Scout:Plugins:Kubernetes:Contexts:0:Name"] = "dev",
                ["Scout:Plugins:YouTube:ApiKey"] = "youtube-dummy-not-real",
                ["Scout:WorkerGate:SocketPath"] = "/run/scout/cp.sock",
                ["Scout:WorkerGate:Token"] = "t0ken",
            })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddScoutHost(configuration).BuildServiceProvider();
        var catalog = new FeatureCatalog();

        var declarations = provider.GetServices<WriteFunctionDeclaration>().ToList();

        declarations.ShouldNotBeEmpty();
        declarations
            .Where(d => !(catalog.Find(d.FeatureId)?.ScopeMatrix?.Any(row => row.Scope == d.Scope
                && row.Access is ScopeAccess.Write or ScopeAccess.IrreversibleWrite) ?? false))
            .Select(d => $"{d.PluginName}.{d.FunctionName} declares {d.FeatureId} / {d.Scope}")
            .ShouldBeEmpty();
    }
}
