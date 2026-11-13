namespace Scout.Sandbox.Tests;

public sealed class SandboxStacksTests : IDisposable
{

    private readonly string _root = Directory.CreateTempSubdirectory("scout-stacks-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Files(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_root, name), "{}");
        }
    }

    [Theory]
    [InlineData("App.slnx", "dotnet restore 'App.slnx'", "dotnet test 'App.slnx' --no-restore")]
    [InlineData("App.sln", "dotnet restore 'App.sln'", "dotnet test 'App.sln' --no-restore")]
    [InlineData("App.csproj", "dotnet restore 'App.csproj'", "dotnet test 'App.csproj' --no-restore")]
    public void A_solution_or_project_at_the_root_is_dotnet_restored_online_and_tested_with_no_restore(string manifest, string restore, string test)
    {
        Files(manifest);

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("dotnet");
        stack.Image.ShouldBe(SandboxImages.DotNet);
        stack.RestoreCommand.ShouldBe(restore);
        stack.TestCommand.ShouldBe(test);
    }

    [Fact]
    public void A_slnx_wins_over_a_sln_and_a_csproj_beside_it()
    {
        Files("B.csproj", "A.sln", "Z.slnx");

        SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull().TestCommand.ShouldBe("dotnet test 'Z.slnx' --no-restore");
    }

    [Fact]
    public void A_project_file_name_with_a_quote_stays_one_shell_word()
    {
        Files("it's.csproj");

        SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull().RestoreCommand.ShouldBe("dotnet restore 'it'\\''s.csproj'");
    }

    [Theory]
    [InlineData("package-lock.json", "npm ci", "npm test")]
    [InlineData("pnpm-lock.yaml", "corepack pnpm install --frozen-lockfile", "corepack pnpm test")]
    [InlineData("yarn.lock", "corepack yarn install --frozen-lockfile", "corepack yarn test")]
    [InlineData(null, "npm install", "npm test")]
    public void A_package_json_is_node_with_the_package_manager_its_lockfile_names(string? lockfile, string restore, string test)
    {
        Files("package.json");
        if (lockfile is not null)
        {
            Files(lockfile);
        }

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("node");
        stack.Image.ShouldBe(SandboxImages.Node);
        stack.RestoreCommand.ShouldBe(restore);
        stack.TestCommand.ShouldBe(test);
        stack.Environment["npm_config_cache"].ShouldBe("/work/cache/npm");
        stack.Environment["COREPACK_HOME"].ShouldBe("/work/cache/corepack");
        stack.OfflineEnvironment["npm_config_offline"].ShouldBe("true");
    }

    [Theory]
    [InlineData("bun.lock", "{\"scripts\":{\"test\":\"vitest run\"}}", "bun run test")]
    [InlineData("bun.lockb", "{\"scripts\":{\"test\":\"vitest run\"}}", "bun run test")]
    [InlineData("bun.lock", "{\"scripts\":{\"build\":\"tsc\"}}", "bun test")]
    [InlineData("bun.lock", "{}", "bun test")]
    [InlineData("bun.lock", "not json", "bun test")]
    public void A_bun_lockfile_runs_in_the_pinned_bun_image_with_its_test_script_or_bun_test(string lockfile, string packageJson, string test)
    {
        Files(lockfile);
        File.WriteAllText(Path.Combine(_root, "package.json"), packageJson);

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("node");
        stack.Image.ShouldBe(SandboxImages.Bun);
        stack.RestoreCommand.ShouldBe("bun install --frozen-lockfile");
        stack.TestCommand.ShouldBe(test);
        stack.Environment["BUN_INSTALL_CACHE_DIR"].ShouldBe("/work/cache/bun");
        stack.Environment["HOME"].ShouldBe("/work/cache/home");
        stack.Note.ShouldBe("No Bun version was declared; ran on Bun 1.4.4.");
    }

    [Fact]
    public void A_bun_lockfile_beside_a_package_lock_runs_with_npm_in_the_node_image()
    {
        Files("package.json", "bun.lock", "package-lock.json");

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Image.ShouldBe(SandboxImages.Node);
        stack.RestoreCommand.ShouldBe("npm ci");
    }

    [Theory]
    [InlineData(new[] { "requirements.txt" }, "python -m venv /work/cache/venv && /work/cache/venv/bin/pip install pytest -r requirements.txt")]
    [InlineData(new[] { "pyproject.toml" }, "python -m venv /work/cache/venv && /work/cache/venv/bin/pip install pytest -e .")]
    [InlineData(new[] { "pyproject.toml", "requirements.txt" }, "python -m venv /work/cache/venv && /work/cache/venv/bin/pip install pytest -r requirements.txt -e .")]
    public void A_python_manifest_is_installed_into_a_venv_under_work_and_tested_with_pytest(string[] manifests, string restore)
    {
        Files(manifests);

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("python");
        stack.Image.ShouldBe(SandboxImages.Python);
        stack.RestoreCommand.ShouldBe(restore);
        stack.TestCommand.ShouldBe("/work/cache/venv/bin/python -m pytest");
        stack.Environment["PIP_CACHE_DIR"].ShouldBe("/work/cache/pip");
        stack.OfflineEnvironment["PIP_NO_INDEX"].ShouldBe("1");
    }

    [Fact]
    public void A_go_mod_is_go_with_its_module_cache_under_work_and_mod_mod()
    {
        Files("go.mod");

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("go");
        stack.Image.ShouldBe(SandboxImages.Go);
        stack.RestoreCommand.ShouldBe("go mod download");
        stack.TestCommand.ShouldBe("go test ./...");
        stack.Environment["GOMODCACHE"].ShouldBe("/work/cache/gomod");
        stack.Environment["GOFLAGS"].ShouldBe("-mod=mod -modcacherw");
        stack.Environment["GOTOOLCHAIN"].ShouldBe("local");
        stack.OfflineEnvironment["GOPROXY"].ShouldBe("off");
    }

    [Fact]
    public void Dotnet_keeps_its_home_and_packages_under_work()
    {
        Files("App.csproj");

        var environment = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull().Environment;

        environment["HOME"].ShouldBe("/work/cache/home");
        environment["DOTNET_CLI_HOME"].ShouldBe("/work/cache/home");
        environment["NUGET_PACKAGES"].ShouldBe("/work/cache/nuget");
    }

    [Fact]
    public void A_dotnet_project_that_declares_nothing_rolls_forward_onto_the_net_10_sdk_and_says_so()
    {
        Files("App.csproj");

        var stack = SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull();

        stack.Environment["DOTNET_ROLL_FORWARD"].ShouldBe("Major");
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Fact]
    public void A_stack_that_refuses_its_version_runs_nothing_even_when_a_later_stack_s_manifest_is_there()
    {
        Files("package.json", "go.mod");
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project><PropertyGroup><TargetFramework>net7.0</TargetFramework></PropertyGroup></Project>");

        SandboxStacks.Detect(_root, null, out var reason).ShouldBeNull();

        reason.ShouldBe("This project targets net7.0; Scout runs .NET 8, 9 and 10.");
    }

    [Theory]
    [InlineData(new[] { "pom.xml", "go.mod", "requirements.txt", "package.json", "App.csproj" }, "dotnet")]
    [InlineData(new[] { "pom.xml", "go.mod", "requirements.txt", "package.json" }, "node")]
    [InlineData(new[] { "build.gradle", "go.mod", "requirements.txt" }, "python")]
    [InlineData(new[] { "pom.xml", "go.mod" }, "go")]
    [InlineData(new[] { "build.gradle", "README.md" }, "java")]
    public void With_several_manifests_the_order_is_dotnet_node_python_go_then_java(string[] manifests, string expected)
    {
        Files(manifests);

        SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull().Name.ShouldBe(expected);
    }

    [Fact]
    public void A_hint_picks_its_stack_among_several_manifests()
    {
        Files("go.mod", "package.json", "App.csproj");

        SandboxStacks.Detect(_root, "Go", out _).ShouldNotBeNull().Name.ShouldBe("go");
    }

    [Theory]
    [InlineData("java")]
    [InlineData(" Java ")]
    public void The_hint_java_picks_java_among_several_manifests(string hint)
    {
        Files("pom.xml", "go.mod", "requirements.txt", "package.json", "App.csproj");

        var stack = SandboxStacks.Detect(_root, hint, out _).ShouldNotBeNull();

        stack.Name.ShouldBe("java");
        stack.Image.ShouldBe(SandboxImages.Maven21);
    }

    [Fact]
    public void The_hint_java_without_a_java_manifest_runs_nothing_and_says_so()
    {
        Files("package.json", "go.mod");

        SandboxStacks.Detect(_root, "java", out var reason).ShouldBeNull();

        reason.ShouldBe("No Java project was found at the repository's root.");
    }

    [Fact]
    public void A_hint_whose_manifest_is_absent_runs_nothing_and_says_so()
    {
        Files("package.json");

        SandboxStacks.Detect(_root, "python", out var reason).ShouldBeNull();

        reason.ShouldBe("No Python project was found at the repository's root.");
    }

    [Fact]
    public void An_unknown_hint_never_becomes_an_image()
    {
        Files("package.json");

        SandboxStacks.Detect(_root, "alpine:latest", out var reason).ShouldBeNull();

        reason.ShouldBe("Scout runs .NET, Node, Python, Go or Java tests, not 'alpine:latest'.");
    }

    [Fact]
    public void A_snapshot_without_a_known_manifest_runs_nothing()
    {
        Files("README.md", "Dockerfile");

        SandboxStacks.Detect(_root, null, out var reason).ShouldBeNull();

        reason.ShouldBe("No .NET, Node, Python, Go or Java project was found at the repository's root.");
    }

    [Fact]
    public void A_manifest_below_the_root_is_not_detected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "App.csproj"), "<Project />");

        SandboxStacks.Detect(_root, null, out _).ShouldBeNull();
    }

    [Fact]
    public void Every_allowlisted_image_is_an_official_image_pinned_by_its_sha256_digest()
    {
        SandboxImages.All.Count.ShouldBe(33);
        foreach (var image in SandboxImages.All)
        {
            image.ShouldMatch("^(mcr\\.microsoft\\.com/dotnet/sdk|node|python|golang|oven/bun|maven|gradle):[0-9][0-9a-z.-]*@sha256:[0-9a-f]{64}$");
        }

        SandboxImages.All.ShouldBe([
            "mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e",
            "node:22@sha256:7702d8aa4d5910871692af4480eb3ba86f23047a6417a44d857d06c95d060d3f",
            "python:3.13@sha256:ecbe213fb818d557fd19db4078244339e2e2e52874f9c81052d3094ea114d797",
            "golang:1.25@sha256:699337d620559a59b4a2bb298ad59611e535d2ee755a34cf2d2a98f37578dc80",
            "oven/bun:1@sha256:1d22548f177f4ff38e0c15baa3c2c46c6c2060ba2d1bc573bb4720b407548f1d",
            "maven:3-eclipse-temurin-8@sha256:c5f4e3545103d845324ab1c541b44bcb931ac28f205835ea1ce5cd9ccec1b2dd",
            "maven:3-eclipse-temurin-11@sha256:433ab11f5f633557a7e5bf7a18c27be315abb44d2e466898a30bd15ee44f7243",
            "maven:3-eclipse-temurin-17@sha256:bec7efd9938efefcf02de53581a19f6816fb6066169a8ea45f52763222ffeb48",
            "maven:3-eclipse-temurin-21@sha256:9673eef9ed503146591667ffb92eaaa22f1b8899ed60d9df0df4da3f3a1214a6",
            "maven:3-eclipse-temurin-25@sha256:047a82fc82c613266cfb1b66fd920be9c8ba51fd67edc68245e4542768f45e36",
            "gradle:7-jdk8@sha256:ec581274b197d53b665373854ed1955a5d186b89b3a440bdac5cfad13532606a",
            "gradle:7-jdk11@sha256:8a25d9da40e2ce1ae24d3ef93bf1c1cb00814c8d9d5611763b20e812b59c85fa",
            "gradle:7-jdk17@sha256:4a69879ab8c693f03bfed585209f221e3d2be20a7cab1e39be7086e89be50ad9",
            "gradle:8-jdk8@sha256:c58ba7d35df44b76998f4fa661331cefde0825b61344da4f2c6b7d216c56ff95",
            "gradle:8-jdk11@sha256:bdd652d6a71711400950d214a3eaf701ff42997e564933265633dd81af000ba7",
            "gradle:8-jdk17@sha256:2f977f0a5b95ade2eabcfcf4840ff4b29fc176c61ee56fd184df038f64b18f9e",
            "gradle:8-jdk21@sha256:930141cf2b9d472f3fd024b2e711856bede114ce116e49fcdfb64427b875982d",
            "gradle:9-jdk17@sha256:55af4a989cc9188c19668b32e52bbd2a0c801da3b849a6e8f5a25e2134845bf5",
            "gradle:9-jdk21@sha256:85f8f572045bfb31874d66d7d8a2bd0c17d8cb9ad34b6a4d02f5d552b4b92c04",
            "gradle:9-jdk25@sha256:e46e15353757ef9c9be0197a243653e88d25ebcd4c3c841057cf839c11fcf898",
            "mcr.microsoft.com/dotnet/sdk:8.0@sha256:341aa6e8bbaceb1194732328ca27c15bfa9c531687db83a66af3c0c1833f5534",
            "mcr.microsoft.com/dotnet/sdk:9.0@sha256:07ed28d9e7279248fcea27334880f6c415b42c631c39b8bc5b3caf13ec6fdaa2",
            "node:18@sha256:c6ae79e38498325db67193d391e6ec1d224d96c693a8a4d943498556716d3783",
            "node:20@sha256:8f693eaa7e0a8e71560c9a82b55fd54c2ae920a2ba5d2cde28bac7d1c01c9ba5",
            "node:24@sha256:8fa96264f302f58bbb13aa7339c541547b56975766ce956c875da149452a3e40",
            "node:26@sha256:438603bbb5cda404b9416f0b5c8026f61501ab911760d861ec28c36b5d675725",
            "python:3.10@sha256:c10791e6f69086d5216f7b5d3b839929c454e24305867ed9bc5152f0355b5423",
            "python:3.11@sha256:731670a425deabbc48d39f0ee3115cfa665b2a4002c19939ad3e88b2832b4549",
            "python:3.12@sha256:cad37dd20c7cac861af9003af8796d03ea7a998ec1e3a664429740fc9d7d2691",
            "python:3.14@sha256:d18055009f327b22993de14458aa5a6bac032cdf459ea2a2e9bbdc3d3d49e4fb",
            "golang:1.23@sha256:60deed95d3888cc5e4d9ff8a10c54e5edc008c6ae3fba6187be6fb592e19e8c0",
            "golang:1.24@sha256:d2d2bc1c84f7e60d7d2438a3836ae7d0c847f4888464e7ec9ba3a1339a1ee804",
            "golang:1.26@sha256:cfa27959837144bf4649802ba38e59daffd0dd2a7994aa83daaf113285b0f551",
        ]);
    }
}
