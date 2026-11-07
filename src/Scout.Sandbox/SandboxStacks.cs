namespace Scout.Sandbox;

/// <summary>
/// A stack Scout knows how to test: the pinned image it runs in, the restore command (run with the network), the
/// test command (run without it), and the variables that keep every cache under <c>/work</c>.
/// </summary>
internal sealed record SandboxStack(
    string Name,
    string Image,
    string RestoreCommand,
    string TestCommand,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> OfflineEnvironment);

/// <summary>The images a run may use, official ones pinned by their multi-architecture index digest.</summary>
internal static class SandboxImages
{
    public const string DotNet = "mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e";
    public const string Node = "node:22@sha256:7702d8aa4d5910871692af4480eb3ba86f23047a6417a44d857d06c95d060d3f";
    public const string Python = "python:3.13@sha256:ecbe213fb818d557fd19db4078244339e2e2e52874f9c81052d3094ea114d797";
    public const string Go = "golang:1.25@sha256:699337d620559a59b4a2bb298ad59611e535d2ee755a34cf2d2a98f37578dc80";

    public static readonly IReadOnlyList<string> All = [DotNet, Node, Python, Go];
}

/// <summary>
/// Picks the stack from the manifests at the snapshot's root, in the order .NET, Node, Python, Go, or the one a hint
/// names, and builds its restore and test commands; never an image or a command from the repository's own files.
/// </summary>
internal static class SandboxStacks
{
    public const string DotNet = "dotnet";
    public const string Node = "node";
    public const string Python = "python";
    public const string Go = "go";

    public const string NoStackReason = "No .NET, Node, Python or Go project was found at the repository's root.";
    public const string BunReason = "The repository uses Bun, which the pinned Node image does not carry; its tests did not run.";

    private const string Home = "/work/cache/home";
    private const string Venv = "/work/cache/venv";

    private static readonly string[] Order = [DotNet, Node, Python, Go];
    private static readonly string[] DotNetManifests = ["*.slnx", "*.sln", "*.csproj"];

    /// <summary>The stack to run, or null with the sentence that says why none runs.</summary>
    public static SandboxStack? Detect(string root, string? hint, out string reason)
    {
        reason = NoStackReason;
        string[] candidates = Order;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var wanted = hint.Trim().ToLowerInvariant();
            if (!Order.Contains(wanted))
            {
                reason = $"Scout runs .NET, Node, Python or Go tests, not '{hint.Trim()}'.";
                return null;
            }

            candidates = [wanted];
            reason = $"No {Label(wanted)} project was found at the repository's root.";
        }

        foreach (var name in candidates)
        {
            if (name == Node && IsBun(root))
            {
                reason = BunReason;
                return null;
            }

            var stack = name switch
            {
                DotNet => ForDotNet(root),
                Node => ForNode(root),
                Python => ForPython(root),
                _ => ForGo(root),
            };
            if (stack is not null)
            {
                return stack;
            }
        }

        return null;
    }

    /// <summary>A value as one single-quoted word for <c>sh -c</c>.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Label(string name) => name switch
    {
        DotNet => ".NET",
        Node => "Node",
        Python => "Python",
        _ => "Go",
    };

    private static bool Has(string root, string file) => File.Exists(Path.Combine(root, file));

    private static bool IsBun(string root) =>
        (Has(root, "bun.lock") || Has(root, "bun.lockb"))
        && Has(root, "package.json")
        && !Has(root, "package-lock.json") && !Has(root, "pnpm-lock.yaml") && !Has(root, "yarn.lock");

    private static SandboxStack? ForDotNet(string root)
    {
        var target = DotNetManifests
            .Select(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault())
            .FirstOrDefault(name => name is not null);
        if (target is null)
        {
            return null;
        }

        return new SandboxStack(
            DotNet,
            SandboxImages.DotNet,
            $"dotnet restore {Quote(target)}",
            $"dotnet test {Quote(target)} --no-restore",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["DOTNET_CLI_HOME"] = Home,
                ["NUGET_PACKAGES"] = "/work/cache/nuget",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_ROLL_FORWARD"] = "Major",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static SandboxStack? ForNode(string root)
    {
        if (!Has(root, "package.json"))
        {
            return null;
        }

        var (restore, test) =
            Has(root, "pnpm-lock.yaml") ? ("corepack pnpm install --frozen-lockfile", "corepack pnpm test")
            : Has(root, "yarn.lock") ? ("corepack yarn install --frozen-lockfile", "corepack yarn test")
            : Has(root, "package-lock.json") ? ("npm ci", "npm test")
            : ("npm install", "npm test");

        return new SandboxStack(
            Node,
            SandboxImages.Node,
            restore,
            test,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["npm_config_cache"] = "/work/cache/npm",
                ["npm_config_update_notifier"] = "false",
                ["COREPACK_HOME"] = "/work/cache/corepack",
                ["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["npm_config_offline"] = "true" });
    }

    private static SandboxStack? ForPython(string root)
    {
        var requirements = Has(root, "requirements.txt");
        var project = Has(root, "pyproject.toml");
        if (!requirements && !project)
        {
            return null;
        }

        var install = $"{Venv}/bin/pip install pytest"
            + (requirements ? " -r requirements.txt" : string.Empty)
            + (project ? " -e ." : string.Empty);

        return new SandboxStack(
            Python,
            SandboxImages.Python,
            $"python -m venv {Venv} && {install}",
            $"{Venv}/bin/python -m pytest",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["PIP_CACHE_DIR"] = "/work/cache/pip",
                ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PIP_NO_INDEX"] = "1" });
    }

    private static SandboxStack? ForGo(string root)
    {
        if (!Has(root, "go.mod"))
        {
            return null;
        }

        return new SandboxStack(
            Go,
            SandboxImages.Go,
            "go mod download",
            "go test ./...",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["GOPATH"] = "/work/cache/gopath",
                ["GOMODCACHE"] = "/work/cache/gomod",
                ["GOCACHE"] = "/work/cache/gobuild",
                ["GOFLAGS"] = "-mod=mod -modcacherw",
                ["GOTOOLCHAIN"] = "local",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["GOPROXY"] = "off" });
    }
}
