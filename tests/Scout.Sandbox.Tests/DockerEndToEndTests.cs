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

    private const string BunPackageJson = """{"name":"fixture","version":"1.0.0","private":true,"scripts":{"test":"bun test"},"dependencies":{"local":"file:./local"}}""";

    private const string BunLock = """
        {
          "lockfileVersion": 2,
          "configVersion": 1,
          "workspaces": {
            "": {
              "name": "fixture",
              "dependencies": {
                "local": "file:./local",
              },
            },
          },
          "packages": {
            "local": ["local@file:local", {}],
          }
        }
        """;

    private const string BunTests = """
        import { test, expect } from "bun:test";
        import { answer } from "local";
        test("the restored dependency resolves", () => expect(answer).toBe(42));
        test("runs as the unprivileged user", () => expect(process.getuid()).not.toBe(0));
        """;

    private const string MavenPom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <modelVersion>4.0.0</modelVersion>
          <groupId>fixture</groupId>
          <artifactId>fixture</artifactId>
          <version>1.0.0</version>
          <properties>
            <maven.compiler.release>17</maven.compiler.release>
            <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
          </properties>
          <dependencies>
            <dependency>
              <groupId>org.junit.jupiter</groupId>
              <artifactId>junit-jupiter</artifactId>
              <version>5.11.4</version>
              <scope>test</scope>
            </dependency>
          </dependencies>
        </project>
        """;

    private const string MavenTest = """
        package fixture;

        import static org.junit.jupiter.api.Assertions.assertEquals;
        import static org.junit.jupiter.api.Assertions.assertNotEquals;

        import org.junit.jupiter.api.Test;

        class FixtureTest {
            @Test
            void runsOnTheJavaTheProjectDeclares() {
                assertEquals("17", System.getProperty("java.specification.version"));
            }

            @Test
            void runsAsTheUnprivilegedUser() throws Exception {
                Process id = new ProcessBuilder("id", "-u").start();
                assertNotEquals("0", new String(id.getInputStream().readAllBytes()).trim());
            }
        }
        """;

    private readonly string _base = Directory.CreateTempSubdirectory("scout-sandbox-e2e-").FullName;
    private readonly string _work = Path.Combine(AppContext.BaseDirectory, "sandbox-e2e", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Directory.Delete(_base, recursive: true);
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

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
        new(new SandboxOptions { WorkDirectory = _work }, TimeProvider.System);

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
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bun_project_restores_and_runs_green_in_the_pinned_bun_image()
    {
        await RequireDocker();
        var root = Path.Combine(_base, "bun");
        Directory.CreateDirectory(Path.Combine(root, "local"));
        File.WriteAllText(Path.Combine(root, "package.json"), BunPackageJson);
        File.WriteAllText(Path.Combine(root, "bun.lock"), BunLock);
        File.WriteAllText(Path.Combine(root, "fixture.test.ts"), BunTests);
        File.WriteAllText(Path.Combine(root, "local", "package.json"), """{"name":"local","version":"1.0.0","main":"index.js"}""");
        File.WriteAllText(Path.Combine(root, "local", "index.js"), "exports.answer = 42;");

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Bun);
        result.Command.ShouldBe("bun run test");
        result.Output.ShouldContain("2 pass");
        result.Output.ShouldContain("0 fail");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_maven_project_that_declares_java_17_runs_green_on_java_17_without_java_on_the_machine()
    {
        await RequireDocker();
        var root = Path.Combine(_base, "maven");
        Directory.CreateDirectory(Path.Combine(root, "src", "test", "java", "fixture"));
        File.WriteAllText(Path.Combine(root, "pom.xml"), MavenPom);
        File.WriteAllText(Path.Combine(root, "src", "test", "java", "fixture", "FixtureTest.java"), MavenTest);

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Maven17);
        result.Command.ShouldBe("mvn -B -ntp -o test");
        result.Note.ShouldBe("Ran on Java 17 with Maven 3.10.0.");
        result.Output.ShouldContain("Tests run: 2, Failures: 0, Errors: 0, Skipped: 0");
        result.Output.ShouldContain("BUILD SUCCESS");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_test_that_reaches_for_the_network_fails_offline()
    {
        await RequireDocker();

        var result = await Runner().RunTestsAsync(new SandboxRequest(Fixture("network", NetworkTest)), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("not ok 1 - reaches the network");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    private string Project(string name, params (string Path, string Content)[] files)
    {
        var root = Path.Combine(_base, name);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }

    [Fact]
    public async Task A_dotnet_project_that_targets_net8_runs_green_on_the_net_8_sdk_and_runtime()
    {
        await RequireDocker();
        var root = Project(
            "dotnet8",
            ("Fixture.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                  </ItemGroup>
                </Project>
                """),
            ("FixtureTest.cs", """
                public class FixtureTest
                {
                    [Xunit.Fact]
                    public void RunsOnTheRuntimeOfTheDeclaredFramework() => Xunit.Assert.Equal(8, System.Environment.Version.Major);
                }
                """));

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.DotNet8);
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 8 SDK.");
        result.Output.ShouldContain("$ mkdir -p /work/cache/home/.local/share/NuGet/Migrations && touch /work/cache/home/.local/share/NuGet/Migrations/1 && dotnet restore 'Fixture.csproj'");
        result.Output.ShouldContain("Passed!");
        result.Output.ShouldContain("Failed:     0, Passed:     1");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_node_project_whose_nvmrc_asks_for_node_20_runs_green_on_node_20()
    {
        await RequireDocker();
        var root = Fixture("node20", """
            const test = require('node:test');
            const assert = require('node:assert');
            test('runs on the node the project declares', () => assert.strictEqual(process.versions.node.split('.')[0], '20'));
            """);
        File.WriteAllText(Path.Combine(root, ".nvmrc"), "20\n");

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Node20);
        result.Note.ShouldBe(".nvmrc asks for Node 20; ran on Node 20.20.2.");
        result.Output.ShouldContain("# pass 1");
        result.Output.ShouldContain("# fail 0");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_python_project_whose_python_version_asks_for_3_11_runs_green_on_python_3_11()
    {
        await RequireDocker();
        var root = Project(
            "python311",
            ("requirements.txt", string.Empty),
            (".python-version", "3.11\n"),
            ("test_fixture.py", "import sys\n\n\ndef test_runs_on_the_python_the_project_declares():\n    assert sys.version_info[:2] == (3, 11)\n"));

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Python311);
        result.Note.ShouldBe(".python-version asks for Python 3.11; ran on Python 3.11.18.");
        result.Output.ShouldContain("1 passed");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_go_module_whose_go_line_asks_for_1_24_runs_green_on_go_1_24()
    {
        await RequireDocker();
        var root = Project(
            "go124",
            ("go.mod", "module example.com/fixture\n\ngo 1.24\n"),
            ("fixture_test.go", """
                package fixture

                import (
                	"runtime"
                	"strings"
                	"testing"
                )

                func TestRunsOnTheGoTheModuleDeclares(t *testing.T) {
                	if !strings.HasPrefix(runtime.Version(), "go1.24.") {
                		t.Fatalf("ran on %s", runtime.Version())
                	}
                }
                """));

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.Go124);
        result.Note.ShouldBe("go.mod asks for Go 1.24 or newer; ran on Go 1.24.13.");
        result.Output.ShouldContain("ok  \texample.com/fixture");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dotnet_project_that_targets_net9_runs_green_on_the_net_9_sdk_and_runtime_without_the_nuget_marker()
    {
        await RequireDocker();
        var root = Project(
            "dotnet9",
            ("Fixture.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                  </ItemGroup>
                </Project>
                """),
            ("FixtureTest.cs", """
                public class FixtureTest
                {
                    [Xunit.Fact]
                    public void RunsOnTheRuntimeOfTheDeclaredFramework() => Xunit.Assert.Equal(9, System.Environment.Version.Major);
                }
                """));

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.DotNet9);
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 9 SDK.");
        result.Output.ShouldContain("$ dotnet restore 'Fixture.csproj'");
        result.Output.ShouldNotContain("NuGet/Migrations");
        result.Output.ShouldContain("Failed:     0, Passed:     1");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dotnet_project_with_a_failing_test_marked_Category_Docker_runs_green_without_it_and_says_so()
    {
        await RequireDocker();
        var root = Project(
            "dotnet-docker-trait",
            ("Fixture.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                  </ItemGroup>
                </Project>
                """),
            ("FixtureTest.cs", """
                public class FixtureTest
                {
                    [Xunit.Fact]
                    public void Passes() => Xunit.Assert.Equal(2, 1 + 1);
                }
                """),
            ("DockerTest.cs", """
                [Xunit.Trait("Category", "Docker")]
                public class DockerTest
                {
                    [Xunit.Fact]
                    public void NeedsADaemon() => Xunit.Assert.Fail("the container has no Docker daemon");
                }
                """));

        var result = await Runner().RunTestsAsync(new SandboxRequest(root), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(0, result.Output);
        result.Image.ShouldBe(SandboxImages.DotNet);
        result.Command.ShouldBe("dotnet test 'Fixture.csproj' --no-restore --filter 'Category!=Docker'");
        result.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK. Left out the tests marked Category=Docker, found in 1 file(s): the container has no Docker daemon.");
        result.Output.ShouldContain("Failed:     0, Passed:     1");
        result.Output.ShouldNotContain("NeedsADaemon");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dotnet_run_whose_failing_test_is_followed_by_more_output_than_the_tail_still_names_the_failing_test()
    {
        await RequireDocker();
        var root = Project(
            "dotnet-failing",
            ("Fixture.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                  </ItemGroup>
                </Project>
                """),
            ("FixtureTest.cs", """
                public class FixtureTest
                {
                    [Xunit.Fact]
                    public void Adds() => Xunit.Assert.Equal(2, 1 + 1);

                    [Xunit.Fact]
                    public void Multiplies() => Xunit.Assert.Equal(6, 2 * 3);

                    [Xunit.Fact]
                    public void Checkout_applies_the_discount() => Xunit.Assert.Equal(90, 100);
                }
                """));
        const string Padding = "i=1; while [ $i -le 200 ]; do echo \"a later project's summary line $i, padding the end of the output\"; i=$((i+1)); done";

        var result = await Runner().RunTestsAsync(new SandboxRequest(root, Command: $"dotnet test 'Fixture.csproj' --no-restore; s=$?; {Padding}; exit $s"), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldBe(1, result.Output);
        result.Output[^4_000..].ShouldNotContain("Checkout_applies_the_discount");
        result.Output.ShouldContain("FixtureTest.Checkout_applies_the_discount [FAIL]");
        result.Output.ShouldContain("Assert.Equal() Failure");
        result.Output.ShouldEndWith("a later project's summary line 200, padding the end of the output\n");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dotnet_run_on_the_microsoft_testing_platform_whose_failing_test_is_followed_by_more_output_than_the_tail_still_names_it()
    {
        await RequireDocker();
        var root = Project(
            "dotnet-mtp-failing",
            ("global.json", """{"test":{"runner":"Microsoft.Testing.Platform"}}"""),
            ("Fixture.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" />
                  </ItemGroup>
                </Project>
                """),
            ("FixtureTest.cs", """
                public class FixtureTest
                {
                    [Xunit.Fact]
                    public void Adds() => Xunit.Assert.Equal(2, 1 + 1);

                    [Xunit.Fact]
                    public void Checkout_applies_the_discount() => Xunit.Assert.Equal(90, 100);
                }
                """));
        const string Padding = "i=1; while [ $i -le 200 ]; do echo \"a later project's summary line $i, padding the end of the output\"; i=$((i+1)); done";

        var result = await Runner().RunTestsAsync(new SandboxRequest(root, Command: $"dotnet test --project 'Fixture.csproj' --no-restore; s=$?; {Padding}; exit $s"), CancellationToken.None);

        result.Ran.ShouldBeTrue(result.Output);
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output[^4_000..].ShouldNotContain("Checkout_applies_the_discount");
        result.Output.ShouldContain("failed FixtureTest.Checkout_applies_the_discount (");
        result.Output.ShouldContain("Assert.Equal() Failure: Values differ");
        Directory.GetDirectories(_work).ShouldBeEmpty();
    }
}
