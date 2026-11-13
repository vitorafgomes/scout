using System.Text.RegularExpressions;

namespace Scout.Sandbox;

/// <summary>
/// The Go stack, <c>go.mod</c> at the root, with <c>GOTOOLCHAIN=local</c> so no toolchain is downloaded. A
/// <c>toolchain</c> directive runs that minor; else the <c>go</c> directive, a minimum, runs the oldest pinned image at
/// or above it; Go 1.25 when go.mod names neither; refused when no pinned image satisfies both.
/// </summary>
internal static partial class SandboxGo
{
    internal static readonly IReadOnlyList<RuntimeImage> Images =
    [
        new("1.23", new Version(1, 23, 12), SandboxImages.Go123),
        new("1.24", new Version(1, 24, 13), SandboxImages.Go124),
        new("1.25", new Version(1, 25, 14), SandboxImages.Go),
        new("1.26", new Version(1, 26, 10), SandboxImages.Go126),
    ];

    private const string DefaultGo = "1.25";

    [GeneratedRegex(@"^\s*go\s+(\d{1,2}\.\d{1,3}(?:\.\d{1,3})?)(?:(?:rc|beta)\d{1,2})?\s*(?://.*)?$")]
    private static partial Regex GoDirective();

    [GeneratedRegex(@"^\s*toolchain\s+go(\d{1,2}\.\d{1,3}(?:\.\d{1,3})?)(?:(?:rc|beta)\d{1,2})?\s*(?://.*)?$")]
    private static partial Regex ToolchainDirective();

    /// <summary>The Go stack, or null: <paramref name="reason"/> untouched without a <c>go.mod</c>, else the refusal.</summary>
    public static SandboxStack? Detect(string root, ref string reason)
    {
        if (!SandboxStacks.Has(root, "go.mod"))
        {
            return null;
        }

        var lines = File.ReadLines(Path.Combine(root, "go.mod")).ToList();
        var go = lines.Select(l => GoDirective().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var toolchain = lines.Select(l => ToolchainDirective().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var minimum = go is null ? new Version(0, 0, 0) : SandboxStacks.Numbers(go)!;

        string note;
        RuntimeImage? image;
        SandboxDeclaredVersion[] declared = [];
        if (toolchain is not null)
        {
            var wanted = SandboxStacks.Numbers(toolchain)!;
            image = Images.FirstOrDefault(i => i.Version.Major == wanted.Major && i.Version.Minor == wanted.Minor && i.Version >= minimum);
            if (image is null)
            {
                reason = Unsupported(minimum > wanted ? $"{go} or newer" : toolchain);
                return null;
            }

            note = SandboxStacks.Declared("Go", "go.mod's toolchain", toolchain, image);
            declared = [new("go.mod's toolchain", $"Go {toolchain}")];
        }
        else if (go is not null)
        {
            image = Images.FirstOrDefault(i => i.Version >= minimum);
            if (image is null)
            {
                reason = Unsupported($"{go} or newer");
                return null;
            }

            note = SandboxStacks.Declared("Go", "go.mod", $"{go} or newer", image);
            declared = [new("go.mod", $"Go {go} or newer")];
        }
        else
        {
            image = Images.First(i => i.Line == DefaultGo);
            note = SandboxStacks.Undeclared("Go", image);
        }

        return new SandboxStack(
            SandboxStacks.Go,
            image.Image,
            "go mod download",
            "go test ./...",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["GOPATH"] = "/work/cache/gopath",
                ["GOMODCACHE"] = "/work/cache/gomod",
                ["GOCACHE"] = "/work/cache/gobuild",
                ["GOFLAGS"] = "-mod=mod -modcacherw",
                ["GOTOOLCHAIN"] = "local",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["GOPROXY"] = "off" },
            note,
            declared,
            $"Go {image.Version}");
    }

    private static string Unsupported(string asked) =>
        $"This project asks for Go {asked}; Scout runs Go {SandboxStacks.Join(Images.Select(i => i.Version.ToString()))}.";
}
