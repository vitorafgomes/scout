using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scout.Sandbox;

/// <summary>
/// The Node stack, <c>package.json</c> at the root: npm, pnpm or yarn by lockfile on the Node the project declares
/// (<c>.nvmrc</c>, then <c>.node-version</c>, then <c>engines.node</c>, then <c>volta.node</c>), or Bun when only a Bun
/// lockfile is there, on the Bun its <c>packageManager</c> or <c>engines.bun</c> declares. A pin runs that major; a
/// range the newest pinned image inside it; nothing declared runs Node 22 or Bun 1; a version no image has is refused.
/// </summary>
internal static partial class SandboxNode
{
    internal static readonly IReadOnlyList<RuntimeImage> Images =
    [
        new("18", new Version(18, 20, 8), SandboxImages.Node18),
        new("20", new Version(20, 20, 2), SandboxImages.Node20),
        new("22", new Version(22, 23, 4), SandboxImages.Node),
        new("24", new Version(24, 22, 2), SandboxImages.Node24),
        new("26", new Version(26, 13, 0), SandboxImages.Node26),
    ];

    internal static readonly IReadOnlyList<RuntimeImage> BunImages =
    [
        new("1", new Version(1, 4, 4), SandboxImages.Bun),
    ];

    private const string DefaultNode = "22";

    private static readonly Dictionary<string, int> LtsNames = new(StringComparer.Ordinal)
    {
        ["argon"] = 4,
        ["boron"] = 6,
        ["carbon"] = 8,
        ["dubnium"] = 10,
        ["erbium"] = 12,
        ["fermium"] = 14,
        ["gallium"] = 16,
        ["hydrogen"] = 18,
        ["iron"] = 20,
        ["jod"] = 22,
        ["krypton"] = 24,
    };

    [GeneratedRegex(@"^v?(\d{1,3})(?:\.\d{1,4}){0,2}$")]
    private static partial Regex Pin();

    [GeneratedRegex(@"^bun@v?(\d{1,3}(?:\.\d{1,4}){0,2})(?:\+[0-9A-Za-z.]{1,200})?$")]
    private static partial Regex BunManager();

    [GeneratedRegex(@"^[0-9xX*.^~<>=|v \t-]{1,100}$")]
    private static partial Regex RangeText();

    [GeneratedRegex(@"(<=|>=|<|>|=|\^|~)?\s*v?(\d{1,4}|[xX*])(?:\.(\d{1,4}|[xX*]))?(?:\.(\d{1,4}|[xX*]))?")]
    private static partial Regex Comparator();

    [GeneratedRegex(@"^(\S+)\s+-\s+(\S+)$")]
    private static partial Regex Hyphen();

    /// <summary>The Node or Bun stack, or null: <paramref name="reason"/> untouched without a <c>package.json</c>, else the refusal.</summary>
    public static SandboxStack? Detect(string root, ref string reason)
    {
        if (!SandboxStacks.Has(root, "package.json"))
        {
            return null;
        }

        var package = Package.Read(Path.Combine(root, "package.json"));
        return IsBun(root) ? ForBun(package, ref reason) : ForNode(root, package, ref reason);
    }

    private static bool IsBun(string root) =>
        (SandboxStacks.Has(root, "bun.lock") || SandboxStacks.Has(root, "bun.lockb"))
        && !SandboxStacks.Has(root, "package-lock.json") && !SandboxStacks.Has(root, "pnpm-lock.yaml") && !SandboxStacks.Has(root, "yarn.lock");

    private static SandboxStack? ForNode(string root, Package package, ref string reason)
    {
        string note;
        RuntimeImage? image;
        SandboxDeclaredVersion[] declared = [];
        if (Declared(root, package) is var (source, asked, chosen))
        {
            if (chosen is null)
            {
                reason = SandboxStacks.Unsupported("Node", asked, Images);
                return null;
            }

            image = chosen;
            note = SandboxStacks.Declared("Node", source, asked, chosen);
            declared = [new(source, $"Node {asked}")];
        }
        else
        {
            image = Images.First(i => i.Line == DefaultNode);
            note = SandboxStacks.Undeclared("Node", image);
        }

        var (restore, test) =
            SandboxStacks.Has(root, "pnpm-lock.yaml") ? ("corepack pnpm install --frozen-lockfile", "corepack pnpm test")
            : SandboxStacks.Has(root, "yarn.lock") ? ("corepack yarn install --frozen-lockfile", "corepack yarn test")
            : SandboxStacks.Has(root, "package-lock.json") ? ("npm ci", "npm test")
            : ("npm install", "npm test");

        return new SandboxStack(
            SandboxStacks.Node,
            image.Image,
            restore,
            test,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["npm_config_cache"] = "/work/cache/npm",
                ["npm_config_update_notifier"] = "false",
                ["COREPACK_HOME"] = "/work/cache/corepack",
                ["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["npm_config_offline"] = "true" },
            note,
            declared,
            $"Node {image.Version}");
    }

    private static SandboxStack? ForBun(Package package, ref string reason)
    {
        (string Source, string Asked, RuntimeImage? Image)? declared =
            package.Manager is { } manager && BunManager().Match(manager) is { Success: true } pinned
                ? ("package.json's packageManager", pinned.Groups[1].Value, PinImage(BunImages, pinned.Groups[1].Value))
                : FromRange(BunImages, "package.json's engines.bun", package.EnginesBun);

        string note;
        RuntimeImage? image;
        SandboxDeclaredVersion[] versions = [];
        if (declared is var (source, asked, chosen))
        {
            if (chosen is null)
            {
                reason = SandboxStacks.Unsupported("Bun", asked, BunImages);
                return null;
            }

            image = chosen;
            note = SandboxStacks.Declared("Bun", source, asked, chosen);
            versions = [new(source, $"Bun {asked}")];
        }
        else
        {
            image = BunImages[0];
            note = SandboxStacks.Undeclared("Bun", image);
        }

        return new SandboxStack(
            SandboxStacks.Node,
            image.Image,
            "bun install --frozen-lockfile",
            package.HasTestScript ? "bun run test" : "bun test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["BUN_INSTALL_CACHE_DIR"] = "/work/cache/bun",
                ["DO_NOT_TRACK"] = "1",
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            note,
            versions,
            $"Bun {image.Version}");
    }

    private static (string Source, string Asked, RuntimeImage? Image)? Declared(string root, Package package)
    {
        foreach (var file in (string[])[".nvmrc", ".node-version"])
        {
            if (SandboxStacks.FirstLine(root, file) is { } line && FromVersionFile(line) is var (asked, image))
            {
                return (file, asked, image);
            }
        }

        if (FromRange(Images, "package.json's engines.node", package.EnginesNode) is { } range)
        {
            return range;
        }

        return package.Volta is { } volta && Pin().IsMatch(volta.Trim())
            ? ("package.json's volta.node", volta.Trim().TrimStart('v'), PinImage(Images, volta.Trim()))
            : null;
    }

    private static (string Asked, RuntimeImage? Image)? FromVersionFile(string line)
    {
        var value = line.ToLowerInvariant();
        if (value is "lts/*" or "node" or "latest" or "current" or "stable")
        {
            return (value, Images[^1]);
        }

        if (value.StartsWith("lts/", StringComparison.Ordinal) && LtsNames.TryGetValue(value[4..], out var major))
        {
            return (value, Images.FirstOrDefault(i => i.Version.Major == major));
        }

        return Pin().IsMatch(value) ? (value.TrimStart('v'), PinImage(Images, value)) : null;
    }

    private static RuntimeImage? PinImage(IReadOnlyList<RuntimeImage> images, string value)
    {
        var major = int.Parse(Pin().Match(value).Groups[1].Value, CultureInfo.InvariantCulture);
        return images.FirstOrDefault(i => i.Version.Major == major);
    }

    private static (string Source, string Asked, RuntimeImage? Image)? FromRange(IReadOnlyList<RuntimeImage> images, string source, string? text)
    {
        var range = text?.Trim();
        return range is null || !RangeText().IsMatch(range) || Range(range) is not { } allows
            ? null
            : (source, range, images.LastOrDefault(i => allows(i.Version)));
    }

    /// <summary>An npm semver range as a test on a version, or null when it is not one this parser reads.</summary>
    internal static Func<Version, bool>? Range(string text)
    {
        var alternatives = new List<Func<Version, bool>>();
        foreach (var raw in text.Split("||"))
        {
            var alternative = raw.Trim();
            if (Hyphen().Match(alternative) is { Success: true } hyphen)
            {
                if (Partial(hyphen.Groups[1].Value) is not { } from || Partial(hyphen.Groups[2].Value) is not { } to)
                {
                    return null;
                }

                alternatives.Add(v => v >= Pad(from) && (to.Length == 0 || (to.Length == 3 ? v <= Pad(to) : v < Next(to))));
                continue;
            }

            var matches = Comparator().Matches(alternative);
            if (Comparator().Replace(alternative, string.Empty).Trim().Length > 0)
            {
                return null;
            }

            var tests = matches.Select(Test).ToList();
            alternatives.Add(v => tests.All(test => test(v)));
        }

        return v => alternatives.Any(test => test(v));
    }

    private static Func<Version, bool> Test(Match match)
    {
        var parts = Parts(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
        if (parts.Length == 0)
        {
            return _ => true;
        }

        var low = Pad(parts);
        return match.Groups[1].Value switch
        {
            ">=" => v => v >= low,
            "<" => v => v < low,
            ">" => parts.Length == 3 ? v => v > low : v => v >= Next(parts),
            "<=" => parts.Length == 3 ? v => v <= low : v => v < Next(parts),
            "^" => v => v >= low && v < new Version(parts[0] + 1, 0, 0),
            "~" => v => v >= low && v < (parts.Length == 1 ? new Version(parts[0] + 1, 0, 0) : new Version(parts[0], parts[1] + 1, 0)),
            _ => v => v >= low && v < Next(parts),
        };
    }

    private static int[]? Partial(string text)
    {
        var match = Comparator().Match(text);
        return match.Success && match.Length == text.Length && !match.Groups[1].Success
            ? Parts(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value)
            : null;
    }

    private static int[] Parts(params string[] values) =>
        [.. values.TakeWhile(p => p.Length > 0 && char.IsAsciiDigit(p[0])).Select(p => int.Parse(p, CultureInfo.InvariantCulture))];

    private static Version Pad(int[] parts) => new(parts[0], parts.Length > 1 ? parts[1] : 0, parts.Length > 2 ? parts[2] : 0);

    private static Version Next(int[] parts) => parts.Length switch
    {
        1 => new Version(parts[0] + 1, 0, 0),
        2 => new Version(parts[0], parts[1] + 1, 0),
        _ => new Version(parts[0], parts[1], parts[2] + 1),
    };

    /// <summary>The fields of <c>package.json</c> the stack reads; all null when it is not a JSON object.</summary>
    private sealed record Package(string? EnginesNode, string? EnginesBun, string? Volta, string? Manager, bool HasTestScript)
    {
        public static Package Read(string path)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new Package(null, null, null, null, false);
                }

                return new Package(
                    Text(root, "engines", "node"),
                    Text(root, "engines", "bun"),
                    Text(root, "volta", "node"),
                    root.TryGetProperty("packageManager", out var manager) && manager.ValueKind == JsonValueKind.String ? manager.GetString() : null,
                    Text(root, "scripts", "test") is not null);
            }
            catch (JsonException)
            {
                return new Package(null, null, null, null, false);
            }
        }

        private static string? Text(JsonElement root, string section, string name) =>
            root.TryGetProperty(section, out var parent)
            && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
