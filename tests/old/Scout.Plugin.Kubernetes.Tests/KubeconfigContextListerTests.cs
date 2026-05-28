using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubeconfigContextListerTests
{
    private const string MultiContextYaml = """
        apiVersion: v1
        kind: Config
        current-context: alpha
        clusters:
        - name: cluster-a
          cluster:
            server: https://1.1.1.1
            certificate-authority-data: Zm9v
        - name: cluster-b
          cluster:
            server: https://2.2.2.2
            certificate-authority-data: Zm9v
        contexts:
        - name: alpha
          context:
            cluster: cluster-a
            user: u
        - name: beta
          context:
            cluster: cluster-b
            user: u
        users:
        - name: u
          user:
            token: t
        """;

    [Fact]
    public void Returns_All_Contexts_In_Declaration_Order()
    {
        var contexts = KubeconfigContextLister.ListContexts(MultiContextYaml);

        contexts.ShouldBe(new[] { "alpha", "beta" });
    }

    [Fact]
    public void Throws_On_Empty_Input()
    {
        Should.Throw<ArgumentException>(() => KubeconfigContextLister.ListContexts(string.Empty));
    }
}
