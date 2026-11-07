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
    [InlineData("bun.lock")]
    [InlineData("bun.lockb")]
    public void A_bun_lockfile_alone_does_not_run_because_the_node_image_carries_no_bun(string lockfile)
    {
        Files("package.json", lockfile);

        SandboxStacks.Detect(_root, null, out var reason).ShouldBeNull();

        reason.ShouldBe("The repository uses Bun, which the pinned Node image does not carry; its tests did not run.");
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
    public void With_several_manifests_dotnet_comes_before_node_python_and_go()
    {
        Files("go.mod", "requirements.txt", "package.json", "App.csproj");

        SandboxStacks.Detect(_root, null, out _).ShouldNotBeNull().Name.ShouldBe("dotnet");
    }

    [Fact]
    public void A_hint_picks_its_stack_among_several_manifests()
    {
        Files("go.mod", "package.json", "App.csproj");

        SandboxStacks.Detect(_root, "Go", out _).ShouldNotBeNull().Name.ShouldBe("go");
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

        reason.ShouldBe("Scout runs .NET, Node, Python or Go tests, not 'alpine:latest'.");
    }

    [Fact]
    public void A_snapshot_without_a_known_manifest_runs_nothing()
    {
        Files("README.md", "Dockerfile");

        SandboxStacks.Detect(_root, null, out var reason).ShouldBeNull();

        reason.ShouldBe("No .NET, Node, Python or Go project was found at the repository's root.");
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
        SandboxImages.All.Count.ShouldBe(4);
        foreach (var image in SandboxImages.All)
        {
            image.ShouldMatch("^(mcr\\.microsoft\\.com/dotnet/sdk|node|python|golang):[0-9.]+@sha256:[0-9a-f]{64}$");
        }

        SandboxImages.All.ShouldBe([
            "mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e",
            "node:22@sha256:7702d8aa4d5910871692af4480eb3ba86f23047a6417a44d857d06c95d060d3f",
            "python:3.13@sha256:ecbe213fb818d557fd19db4078244339e2e2e52874f9c81052d3094ea114d797",
            "golang:1.25@sha256:699337d620559a59b4a2bb298ad59611e535d2ee755a34cf2d2a98f37578dc80",
        ]);
    }
}
