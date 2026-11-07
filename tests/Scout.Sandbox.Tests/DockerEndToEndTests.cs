namespace Scout.Sandbox.Tests;

[Trait("Category", "Docker")]
public sealed class DockerEndToEndTests : IDisposable
{
    private const string SecretName = "SCOUT_SANDBOX_E2E_SECRET";
    private const string SecretValue = "e2e-owner-secret-must-not-enter";

    private const string PackageJson = """{"name":"fixture","version":"1.0.0","private":true,"scripts":{"test":"node --test"}}""";
    private const string PackageLock = """{"name":"fixture","version":"1.0.0","lockfileVersion":3,"requires":true,"packages":{"":{"name":"fixture","version":"1.0.0"}}}""";

    private const string IsolationTests = """
        const test = require('node:test');
        const assert = require('node:assert');
        const fs = require('node:fs');
        test('adds', () => assert.strictEqual(1 + 1, 2));
        test('runs as the unprivileged user', () => assert.notStrictEqual(process.getuid(), 0));
        test('the root filesystem is read-only', () => assert.ok(fs.readFileSync('/proc/mounts', 'utf8').split('\n').find(line => line.split(' ')[1] === '/').split(' ')[3].split(',').includes('ro')));
        test('the work folder is writable', () => fs.writeFileSync('/work/src/probe.txt', 'x'));
        test('no variable of the owner enters', () => assert.strictEqual(process.env.SCOUT_SANDBOX_E2E_SECRET, undefined));
        """;

    private const string NetworkTest = """
        const test = require('node:test');
        test('reaches the network', async () => { await fetch('https://example.com'); });
        """;

    private readonly string _base = Directory.CreateTempSubdirectory("scout-sandbox-e2e-").FullName;

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Fixture(string name, string testFile)
    {
        var root = Path.Combine(_base, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), PackageJson);
        File.WriteAllText(Path.Combine(root, "package-lock.json"), PackageLock);
        File.WriteAllText(Path.Combine(root, "fixture.test.js"), testFile);
        return root;
    }

    private static async Task RequireDocker()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
        int exit;
        try
        {
            exit = await SystemProcessRunner.Instance.RunAsync(new ProcessCall("docker", ["version", "--format", "{{.Server.Version}}"], environment), _ => { }, CancellationToken.None);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            exit = -1;
        }

        exit.ShouldBe(0, "this end-to-end test needs a reachable Docker daemon (set DOCKER_CONTEXT or DOCKER_HOST); without one, run the suite with --filter Category!=Docker");
    }

    private DockerSandboxRunner Runner() =>
        new(new SandboxOptions { WorkDirectory = Path.Combine(_base, "work") }, TimeProvider.System);

    [Fact]
    public async Task A_node_project_runs_green_in_the_container_isolated_from_the_owner()
    {
        await RequireDocker();
        Environment.SetEnvironmentVariable(SecretName, SecretValue);
        DockerSandboxRunner runner;
        try
        {
            runner = Runner();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, null);
        }

        var result = await runner.RunTestsAsync(new SandboxRequest(Fixture("hello", IsolationTests)), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Node);
        result.Command.ShouldBe("npm test");
        result.Output.ShouldContain("# pass 5");
        result.Output.ShouldNotContain(SecretValue);
        Directory.GetDirectories(Path.Combine(_base, "work")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_test_that_reaches_for_the_network_fails_offline()
    {
        await RequireDocker();

        var result = await Runner().RunTestsAsync(new SandboxRequest(Fixture("network", NetworkTest)), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("not ok 1 - reaches the network");
        Directory.GetDirectories(Path.Combine(_base, "work")).ShouldBeEmpty();
    }
}
