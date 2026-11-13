using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Scout.Sandbox;

/// <summary>
/// The .NET stack: the first <c>*.slnx</c>, else <c>*.sln</c>, else <c>*.csproj</c> at the root. The SDK is the one
/// <c>global.json</c> asks for when the image's SDK satisfies its <c>rollForward</c> policy, or a refusal naming both;
/// without a <c>global.json</c> SDK, the SDK of the highest target framework of the solution's projects, with
/// roll-forward so the lower frameworks' tests run on its runtime; .NET 10 when nothing is declared.
/// </summary>
internal static partial class SandboxDotNet
{
    internal static readonly IReadOnlyList<RuntimeImage> Images =
    [
        new("8", new Version(8, 0, 427), SandboxImages.DotNet8),
        new("9", new Version(9, 0, 320), SandboxImages.DotNet9),
        new("10", new Version(10, 0, 403), SandboxImages.DotNet),
    ];

    private const string DefaultPolicy = "latestPatch";

    /// <summary>
    /// Marks NuGet's one migration done in the fresh home before an SDK 8 restore, because the .NET 8 SDK's first run
    /// takes a named mutex for it that fails for a user the image's <c>/etc/passwd</c> does not list, which the owner's
    /// uid is; the SDK 9 and 10 images run it without the marker.
    /// </summary>
    internal const string NuGetMigrated = "mkdir -p /work/cache/home/.local/share/NuGet/Migrations && touch /work/cache/home/.local/share/NuGet/Migrations/1";

    private static readonly string[] Manifests = ["*.slnx", "*.sln", "*.csproj"];

    private static readonly string[] Policies = ["patch", "feature", "minor", "major", "latestPatch", "latestFeature", "latestMinor", "latestMajor", "disable"];

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d)(\d{2})(?:-[0-9A-Za-z.-]{1,64})?$")]
    private static partial Regex SdkVersion();

    [GeneratedRegex(@"^net(\d{1,3})\.(\d{1,3})(?:-[A-Za-z0-9.]{1,32})?$")]
    private static partial Regex TargetFramework();

    [GeneratedRegex(@"""([^""\r\n]{1,260}\.(?:cs|fs|vb)proj)""")]
    private static partial Regex SlnProject();

    [GeneratedRegex(@"Trait\(\s*""Category""\s*,\s*""Docker""\s*\)")]
    private static partial Regex DockerTrait();

    private const string DockerFilter = "Category!=Docker";

    private const long MaxSourceBytes = 1024 * 1024;

    private static readonly string[] UnscannedFolders = ["bin", "obj", ".git", "node_modules"];

    private static readonly EnumerationOptions SourceWalk = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

    /// <summary>The .NET stack, or null: <paramref name="reason"/> untouched without a .NET manifest, else the refusal.</summary>
    public static SandboxStack? Detect(string root, ref string reason)
    {
        var target = Manifests
            .Select(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault())
            .FirstOrDefault(name => name is not null);
        if (target is null)
        {
            return null;
        }

        var frameworks = TargetFrameworks(root, target);
        var (sdk, policy) = GlobalJson(root);
        RuntimeImage? image;
        string head;
        bool rollForward;
        SandboxDeclaredVersion[] declared;
        if (sdk is not null)
        {
            image = ForGlobalJson(sdk, policy);
            if (image is null)
            {
                reason = $"This project's global.json asks for SDK {sdk} with rollForward {policy}; Scout runs SDK {SandboxStacks.Join(Images.Select(i => i.Version.ToString()))}.";
                return null;
            }

            head = $"global.json asks for SDK {sdk} with rollForward {policy}; ran on SDK {image.Version}";
            declared = [new("global.json", $"SDK {sdk} with rollForward {policy}")];
            rollForward = policy is "major" or "latestMajor";
        }
        else
        {
            var highest = frameworks.Count == 0 ? Images[^1].Version : frameworks[^1].Version;
            image = Images.FirstOrDefault(i => i.Version.Major == highest.Major);
            if (image is null)
            {
                reason = $"This project targets {frameworks[^1].Name}; Scout runs .NET {SandboxStacks.Join(Images.Select(i => i.Line))}.";
                return null;
            }

            head = $"No SDK was declared; ran on the .NET {image.Line} SDK";
            declared = frameworks.Count == 0 ? [] : [new(target, SandboxStacks.Join(frameworks.Select(f => f.Name)))];
            rollForward = true;
        }

        var lower = frameworks.Where(f => f.Version.Major < image.Version.Major).Select(f => f.Name).ToList();
        var tail = rollForward && lower.Count > 0 ? $"; {SandboxStacks.Join(lower)} tests ran on the .NET {image.Line} runtime (roll-forward)" : string.Empty;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = SandboxStacks.Home,
            ["DOTNET_CLI_HOME"] = SandboxStacks.Home,
            ["NUGET_PACKAGES"] = "/work/cache/nuget",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
        if (rollForward)
        {
            environment["DOTNET_ROLL_FORWARD"] = "Major";
        }

        var marked = FilesMarkedDocker(root);
        return new SandboxStack(
            SandboxStacks.DotNet,
            image.Image,
            image.Version.Major == 8 ? $"{NuGetMigrated} && dotnet restore {SandboxStacks.Quote(target)}" : $"dotnet restore {SandboxStacks.Quote(target)}",
            marked > 0 ? $"dotnet test {SandboxStacks.Quote(target)} --no-restore --filter {SandboxStacks.Quote(DockerFilter)}" : $"dotnet test {SandboxStacks.Quote(target)} --no-restore",
            environment,
            new Dictionary<string, string>(StringComparer.Ordinal),
            $"{head}{tail}.",
            declared,
            $"SDK {image.Version}",
            marked > 0 ? $"Left out the tests marked Category=Docker, found in {marked} file(s): the container has no Docker daemon." : null);
    }

    /// <summary>
    /// How many <c>*.cs</c> files under <paramref name="folder"/> mark tests <c>Trait("Category", "Docker")</c>, read
    /// as text; <c>bin</c>, <c>obj</c>, <c>.git</c>, <c>node_modules</c>, symbolic links, files over 1 MB and files
    /// that cannot be read are not counted.
    /// </summary>
    private static int FilesMarkedDocker(string folder)
    {
        var count = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SourceWalk))
        {
            if (entry is DirectoryInfo directory)
            {
                count += UnscannedFolders.Contains(directory.Name, StringComparer.Ordinal) ? 0 : FilesMarkedDocker(directory.FullName);
            }
            else if (entry is FileInfo { Extension: ".cs" } file && file.Length <= MaxSourceBytes && MarksDocker(file.FullName))
            {
                count++;
            }
        }

        return count;
    }

    private static bool MarksDocker(string path)
    {
        try
        {
            return DockerTrait().IsMatch(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RuntimeImage? ForGlobalJson(string sdk, string policy)
    {
        var match = SdkVersion().Match(sdk);
        var asked = new Version(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value + match.Groups[4].Value, CultureInfo.InvariantCulture));
        var band = asked.Build / 100;
        bool Accepts(Version image) => policy switch
        {
            "disable" => image == asked,
            "patch" or "latestPatch" => image.Major == asked.Major && image.Minor == asked.Minor && image.Build / 100 == band && image >= asked,
            "feature" or "latestFeature" => image.Major == asked.Major && image.Minor == asked.Minor && image >= asked,
            "minor" or "latestMinor" => image.Major == asked.Major && image >= asked,
            _ => image >= asked,
        };

        var accepted = Images.Where(i => Accepts(i.Version)).ToList();
        return policy == "latestMajor" ? accepted.LastOrDefault() : accepted.FirstOrDefault();
    }

    private static (string? Sdk, string Policy) GlobalJson(string root)
    {
        if (!SandboxStacks.Has(root, "global.json"))
        {
            return (null, DefaultPolicy);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sdk", out var sdk)
                || sdk.ValueKind != JsonValueKind.Object
                || !sdk.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String
                || !SdkVersion().IsMatch(version.GetString()!))
            {
                return (null, DefaultPolicy);
            }

            var policy = sdk.TryGetProperty("rollForward", out var roll) && roll.ValueKind == JsonValueKind.String
                ? Policies.FirstOrDefault(p => string.Equals(p, roll.GetString(), StringComparison.OrdinalIgnoreCase)) ?? DefaultPolicy
                : DefaultPolicy;
            return (version.GetString(), policy);
        }
        catch (JsonException)
        {
            return (null, DefaultPolicy);
        }
    }

    private static List<(string Name, Version Version)> TargetFrameworks(string root, string target)
    {
        var projects = target.EndsWith(".slnx", StringComparison.Ordinal) ? SlnxProjects(Path.Combine(root, target))
            : target.EndsWith(".sln", StringComparison.Ordinal) ? SlnProjects(Path.Combine(root, target))
            : Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>();

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var found = new Dictionary<string, Version>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var relative = project.Replace('\\', '/');
            if (Path.IsPathRooted(relative))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(full, StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                continue;
            }

            var own = ProjectFrameworks(path);
            foreach (var name in own.Count > 0 ? own : PropsFrameworks(full, Path.GetDirectoryName(path)!))
            {
                var match = TargetFramework().Match(name);
                var major = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                if (major >= 5)
                {
                    found.TryAdd($"net{match.Groups[1].Value}.{match.Groups[2].Value}", new Version(major, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
                }
            }
        }

        return [.. found.Select(f => (f.Key, f.Value)).OrderBy(f => f.Value)];
    }

    /// <summary>
    /// The frameworks of the nearest <c>Directory.Build.props</c> that declares any, from the project's folder up to the
    /// snapshot's root; a props file that is a symbolic link is skipped, and what it imports is never read.
    /// </summary>
    private static List<string> PropsFrameworks(string root, string folder)
    {
        for (var current = folder; (current + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal); current = Path.GetDirectoryName(current)!)
        {
            var props = Path.Combine(current, "Directory.Build.props");
            if (File.Exists(props) && new FileInfo(props).LinkTarget is null && ProjectFrameworks(props) is { Count: > 0 } frameworks)
            {
                return frameworks;
            }

            if (current + Path.DirectorySeparatorChar == root)
            {
                break;
            }
        }

        return [];
    }

    private static List<string> ProjectFrameworks(string path) =>
        Load(path)?.Root?.Descendants()
            .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(f => f.ToLowerInvariant())
            .ToList() ?? [];

    private static List<string> SlnxProjects(string path) =>
        Load(path)?.Root?.Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => (string?)e.Attribute("Path"))
            .OfType<string>()
            .ToList() ?? [];

    private static List<string> SlnProjects(string path) =>
        File.ReadLines(path)
            .Where(l => l.StartsWith("Project(", StringComparison.Ordinal))
            .Select(l => SlnProject().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

    private static XDocument? Load(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
