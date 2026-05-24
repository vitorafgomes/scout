using System.Net;
using System.Net.Http;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Core.Features;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes.Tests;

public sealed class KubeconfigValidatorTests
{
    private const string ValidKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: ctx-a
        clusters:
        - name: cluster-a
          cluster:
            server: https://127.0.0.1:6443
            certificate-authority-data: Zm9v
        contexts:
        - name: ctx-a
          context:
            cluster: cluster-a
            user: user-a
        users:
        - name: user-a
          user:
            token: abcdef
        """;

    private const string ExecKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: ctx-a
        clusters:
        - name: cluster-a
          cluster:
            server: https://127.0.0.1:6443
            certificate-authority-data: Zm9v
        contexts:
        - name: ctx-a
          context:
            cluster: cluster-a
            user: user-a
        users:
        - name: user-a
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: gke-gcloud-auth-plugin
        """;

    private static IOptions<KubernetesPluginOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new KubernetesPluginOptions());

    [Fact]
    public async Task Returns_Malformed_When_Kubeconfig_Missing()
    {
        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => Substitute.For<IKubernetes>());

        var result = await validator.ValidateAsync(
            new Dictionary<string, string>(), CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Malformed);
        result.Detail!.ShouldContain("required");
    }

    [Fact]
    public async Task Returns_Malformed_When_Yaml_Garbage()
    {
        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => Substitute.For<IKubernetes>());

        var result = await validator.ValidateAsync(
            new Dictionary<string, string> { ["kubeconfig"] = "::: not yaml :::" },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Malformed);
    }

    [Fact]
    public async Task Returns_UnsupportedAuth_When_Exec_Block_Present()
    {
        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => Substitute.For<IKubernetes>());

        var result = await validator.ValidateAsync(
            new Dictionary<string, string> { ["kubeconfig"] = ExecKubeconfig },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.UnsupportedAuth);
        result.Detail!.ShouldContain("Exec-plugin");
    }

    [Fact]
    public async Task Returns_Valid_When_Probe_Succeeds()
    {
        var probe = Substitute.For<IKubernetes>();
        var versionOps = Substitute.For<IVersionOperations>();
        var coreOps = Substitute.For<ICoreV1Operations>();
        probe.Version.Returns(versionOps);
        probe.CoreV1.Returns(coreOps);

        versionOps.GetCodeWithHttpMessagesAsync(Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(new HttpOperationResponse<VersionInfo>
            {
                Body = new VersionInfo { GitVersion = "v1.30.0" },
                Response = new HttpResponseMessage(HttpStatusCode.OK),
            });

        coreOps.ListNamespaceWithHttpMessagesAsync(
                Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(new HttpOperationResponse<V1NamespaceList>
            {
                Body = new V1NamespaceList { Items = new List<V1Namespace>() },
                Response = new HttpResponseMessage(HttpStatusCode.OK),
            });

        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => probe);

        var result = await validator.ValidateAsync(
            new Dictionary<string, string> { ["kubeconfig"] = ValidKubeconfig },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Valid);
        result.Detail.ShouldBe("v1.30.0");
        result.Metadata.ShouldNotBeNull();
        result.Metadata!["serverVersion"].ShouldBe("v1.30.0");
        result.Metadata["contexts"].ShouldBe("ctx-a");
    }

    [Fact]
    public async Task Returns_MissingScopes_When_Probe_Returns_403()
    {
        var probe = Substitute.For<IKubernetes>();
        var versionOps = Substitute.For<IVersionOperations>();
        probe.Version.Returns(versionOps);

        versionOps.GetCodeWithHttpMessagesAsync(Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<VersionInfo>>>(_ => throw new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), string.Empty),
            });

        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => probe);

        var result = await validator.ValidateAsync(
            new Dictionary<string, string> { ["kubeconfig"] = ValidKubeconfig },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.MissingScopes);
    }

    [Fact]
    public async Task Returns_Unreachable_When_HttpRequestException()
    {
        var probe = Substitute.For<IKubernetes>();
        var versionOps = Substitute.For<IVersionOperations>();
        probe.Version.Returns(versionOps);

        versionOps.GetCodeWithHttpMessagesAsync(Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<VersionInfo>>>(_ => throw new HttpRequestException("connection refused"));

        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => probe);

        var result = await validator.ValidateAsync(
            new Dictionary<string, string> { ["kubeconfig"] = ValidKubeconfig },
            CancellationToken.None);

        result.Outcome.ShouldBe(FeatureCredentialValidationOutcome.Unreachable);
        result.Detail!.ShouldContain("connection refused");
    }

    [Fact]
    public void FeatureId_Is_kubernetes()
    {
        var validator = new KubeconfigValidator(Options(), NullLogger<KubeconfigValidator>.Instance, _ => Substitute.For<IKubernetes>());

        validator.FeatureId.ShouldBe("kubernetes");
    }
}
