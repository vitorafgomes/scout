using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Scout.Sandbox.Tests;

public sealed class SandboxServiceCollectionExtensionsTests
{
    [Fact]
    public void The_runner_resolves_without_creating_its_work_folder()
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), $"scout-sandbox-di-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Sandbox:WorkDirectory"] = workDirectory })
            .Build();

        using var provider = new ServiceCollection().AddSandbox(configuration).BuildServiceProvider();

        provider.GetRequiredService<DockerSandboxRunner>().ShouldNotBeNull();
        Directory.Exists(workDirectory).ShouldBeFalse();
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_configured_docker_path_is_the_one_the_runner_calls()
    {
        var root = Directory.CreateTempSubdirectory("scout-sandbox-di-").FullName;
        try
        {
            var snapshot = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
            File.WriteAllText(Path.Combine(snapshot, "go.mod"), "module example.com/hello");
            var marker = Path.Combine(root, "called-with.txt");
            var fakeDocker = Path.Combine(root, "fake-docker");
            File.WriteAllText(fakeDocker, $"#!/bin/sh\necho \"$@\" > '{marker}'\nexit 1\n");
            File.SetUnixFileMode(fakeDocker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scout:Sandbox:WorkDirectory"] = Path.Combine(root, "work"),
                    ["Scout:Sandbox:DockerPath"] = fakeDocker,
                })
                .Build();
            using var provider = new ServiceCollection().AddSandbox(configuration).BuildServiceProvider();

            var result = await provider.GetRequiredService<DockerSandboxRunner>().RunTestsAsync(new SandboxRequest(snapshot), CancellationToken.None);

            File.Exists(marker).ShouldBeTrue("the runner did not call the configured DockerPath");
            File.ReadAllText(marker).Trim().ShouldBe("version --format {{.Server.Version}}");
            result.Reason.ShouldBe("Docker is not installed or not running.");
            result.Image.ShouldBeNull();
            result.Command.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_configuration_the_work_folder_is_under_scouts_local_data_folder()
    {
        new SandboxOptions().WorkDirectory.ShouldBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "sandbox"));
    }
}
