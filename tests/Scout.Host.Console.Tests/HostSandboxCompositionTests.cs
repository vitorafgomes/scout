using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Plugin.GitHub.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostSandboxCompositionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("scout-host-sandbox-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string WorkDirectory => Path.Combine(_folder, "work");

    private IHost Host(bool github, string? dockerPath = null, string? enabled = "true")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "Ollama",
            ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
            ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
            ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
            ["Scout:Plugins:GitHub:SnapshotsDirectory"] = Path.Combine(_folder, "snapshots"),
            ["Scout:Sandbox:WorkDirectory"] = WorkDirectory,
            ["Scout:Sandbox:DockerPath"] = dockerPath ?? Path.Combine(_folder, "no-docker-here"),
        };
        if (github)
        {
            settings["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real";
        }

        if (enabled is not null)
        {
            settings["Scout:Sandbox:Enabled"] = enabled;
        }

        return new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            })
            .Build();
    }

    private string Snapshot(params string[] files)
    {
        var root = Directory.CreateDirectory(Path.Combine(_folder, "snapshot")).FullName;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(root, file), "<Project />");
        }

        return root;
    }

    [UnsupportedOSPlatform("windows")]
    private string RecordingDocker()
    {
        var docker = Path.Combine(_folder, "recording-docker");
        File.WriteAllText(docker, $"#!/bin/sh\necho \"$*\" >> '{DockerCalls}'\nexit 0\n");
        File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return docker;
    }

    private string DockerCalls => Path.Combine(_folder, "docker-calls.txt");

    [Fact]
    public void With_github_configured_the_plugins_sandbox_runner_is_the_docker_sandbox_adapter()
    {
        using var host = Host(github: true);

        host.Services.GetRequiredService<ISandboxRunner>().ShouldBeOfType<SandboxRunnerAdapter>();
        Directory.Exists(WorkDirectory).ShouldBeFalse();
    }

    [Fact]
    public void Without_github_no_sandbox_runner_is_registered()
    {
        using var host = Host(github: false);

        host.Services.GetService<ISandboxRunner>().ShouldBeNull();
    }

    [Fact]
    public async Task With_no_docker_reachable_the_runner_answers_that_nothing_ran_with_the_docker_sentence()
    {
        using var host = Host(github: true);
        var runner = host.Services.GetRequiredService<ISandboxRunner>();

        var result = await runner.RunTestsAsync(new SandboxRunRequest(Snapshot("App.csproj"), null, null), CancellationToken.None);

        result.ShouldBe(new SandboxRunResult(false, "Docker is not installed or not running.", null, null, null, 0, string.Empty, null));
        Directory.Exists(WorkDirectory).ShouldBeFalse();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_adapter_passes_the_request_through_and_brings_every_field_of_the_result_back()
    {
        var docker = Path.Combine(_folder, "fake-docker");
        File.WriteAllText(docker, "#!/bin/sh\ncase \"$1\" in version) exit 0;; kill) exit 0;; esac\necho \"fake run $*\"\ncase \"$*\" in *-test\\ *) exit 3;; esac\nexit 0\n");
        File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var host = Host(github: true, dockerPath: docker);
        var runner = host.Services.GetRequiredService<ISandboxRunner>();
        var snapshot = Snapshot("App.csproj", "go.mod");

        var dotnet = await runner.RunTestsAsync(new SandboxRunRequest(snapshot, null, "dotnet test --filter Unit"), CancellationToken.None);
        var go = await runner.RunTestsAsync(new SandboxRunRequest(snapshot, "go", null), CancellationToken.None);

        dotnet.Ran.ShouldBeTrue(dotnet.Output);
        dotnet.Reason.ShouldBeNull();
        dotnet.Image.ShouldNotBeNull().ShouldStartWith("mcr.microsoft.com/dotnet/sdk:10.0@sha256:");
        dotnet.Command.ShouldBe("dotnet test --filter Unit");
        dotnet.ExitCode.ShouldBe(3);
        dotnet.Output.ShouldContain("$ dotnet test --filter Unit");
        dotnet.Output.ShouldContain("fake run run --rm --name scout-sandbox-");
        dotnet.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
        go.Image.ShouldNotBeNull().ShouldStartWith("golang:1.25@sha256:");
        go.Command.ShouldBe("go test ./...");
        go.Note.ShouldBe("No Go version was declared; ran on Go 1.25.14.");
        Directory.GetDirectories(WorkDirectory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("not-a-bool")]
    [UnsupportedOSPlatform("windows")]
    public async Task With_docker_runs_off_in_scout_desktop_nothing_runs_and_the_reason_says_where_to_turn_them_on(string? enabled)
    {
        using var host = Host(github: true, dockerPath: RecordingDocker(), enabled: enabled);
        var runner = host.Services.GetRequiredService<ISandboxRunner>();

        var result = await runner.RunTestsAsync(new SandboxRunRequest(Snapshot("App.csproj"), null, null), CancellationToken.None);

        result.ShouldBe(new SandboxRunResult(false, "Docker runs are off in Scout Desktop; turn them on in Features, Docker.", null, null, null, 0, string.Empty, null));
        File.Exists(DockerCalls).ShouldBeFalse("docker must not be called while Docker runs are off");
        Directory.Exists(WorkDirectory).ShouldBeFalse();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task With_docker_runs_on_in_scout_desktop_the_run_goes_through_to_docker()
    {
        using var host = Host(github: true, dockerPath: RecordingDocker(), enabled: "true");
        var runner = host.Services.GetRequiredService<ISandboxRunner>();

        var result = await runner.RunTestsAsync(new SandboxRunRequest(Snapshot("App.csproj"), null, null), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Reason);
        result.Image.ShouldNotBeNull().ShouldStartWith("mcr.microsoft.com/dotnet/sdk:10.0@sha256:");
        File.ReadAllText(DockerCalls).ShouldContain("run --rm --name scout-sandbox-");
    }

    [Theory]
    [InlineData("true")]
    [InlineData(null)]
    public void With_github_configured_the_plugins_stack_detector_reads_a_snapshot_with_the_sandbox_s_detection_whether_docker_runs_are_on_or_off(string? enabled)
    {
        using var host = Host(github: true, enabled: enabled);
        var go = Directory.CreateDirectory(Path.Combine(_folder, "go")).FullName;
        File.WriteAllText(Path.Combine(go, "go.mod"), "module example.com/m\n\ngo 1.24\n");
        var node = Directory.CreateDirectory(Path.Combine(_folder, "node")).FullName;
        File.WriteAllText(Path.Combine(node, "package.json"), "{}");
        File.WriteAllText(Path.Combine(node, ".nvmrc"), "16\n");

        var detector = host.Services.GetRequiredService<IStackDetector>().ShouldBeOfType<StackDetectorAdapter>();
        var goStack = detector.Detect(go);
        var refused = detector.Detect(node);

        (goStack.Stack, goStack.Runs, goStack.Refusal).ShouldBe(("Go", "Go 1.24.13", (string?)null));
        goStack.Image.ShouldNotBeNull().ShouldStartWith("golang:1.24@sha256:");
        goStack.Declared.ShouldBe([new DeclaredVersion("go.mod", "Go 1.24 or newer")]);
        (refused.Stack, refused.Image, refused.Runs, refused.Refusal).ShouldBe(("Node", (string?)null, (string?)null, (string?)"This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26."));
        refused.Declared.ShouldBeEmpty();
        Directory.Exists(WorkDirectory).ShouldBeFalse("detection runs nothing and writes nothing in the work folder");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_adapter_passes_the_repository_and_the_pull_request_to_the_run_s_record()
    {
        using var host = Host(github: true, dockerPath: RecordingDocker(), enabled: "true");
        var runner = host.Services.GetRequiredService<ISandboxRunner>();

        await runner.RunTestsAsync(new SandboxRunRequest(Snapshot("App.csproj"), null, null, "octocat/hello", 42), CancellationToken.None);

        var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(WorkDirectory, "*.run.json").ShouldHaveSingleItem())).RootElement;
        record.GetProperty("repository").GetString().ShouldBe("octocat/hello");
        record.GetProperty("pullRequest").GetInt32().ShouldBe(42);
        record.GetProperty("stack").GetString().ShouldBe(".NET");
        record.GetProperty("result").GetString().ShouldBe("passed");
    }
}
