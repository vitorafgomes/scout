using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Scout.Sandbox;

/// <summary>One pinned Java image: its build tool, the tool version it carries, the JDK it runs and the image by digest.</summary>
internal sealed record JavaImage(string Tool, int ToolMajor, string ToolVersion, int Java, string Image);

/// <summary>
/// The Java stack: Maven when a <c>pom.xml</c> is at the root, else Gradle when a build or settings file is. The JDK is
/// the one the project declares, read from its files as text or XML and never by running the build, mapped to a pinned
/// image; Java 21 when none is declared, said in the note; a refusal naming both versions when no image has it. Gradle
/// runs the image's own <c>gradle</c>, of the major the wrapper declares, and never the repository's wrapper.
/// </summary>
internal static partial class SandboxJava
{
    public const int DefaultJava = 21;
    public const int DefaultGradle = 8;

    internal const string MavenRestore = "mvn -B -ntp test -Dtest='*#scoutRestoreSelectsNoTest' -Dsurefire.failIfNoSpecifiedTests=false";
    internal const string MavenTest = "mvn -B -ntp -o test";
    internal const string GradleRestore = "printf '%s' 'allprojects { tasks.register(\"scoutResolve\") { inputs.files(project.provider { project.configurations.findByName(\"testRuntimeClasspath\") ?: project.files() }); doLast { } } }' > /work/cache/scout-restore.gradle && gradle --no-daemon -I /work/cache/scout-restore.gradle testClasses scoutResolve";
    internal const string GradleTest = "gradle --no-daemon --offline test";

    private const string Maven = "Maven";
    private const string Gradle = "Gradle";

    internal static readonly IReadOnlyList<JavaImage> Images =
    [
        new(Maven, 3, "3.10.0", 8, SandboxImages.Maven8),
        new(Maven, 3, "3.10.0", 11, SandboxImages.Maven11),
        new(Maven, 3, "3.10.0", 17, SandboxImages.Maven17),
        new(Maven, 3, "3.10.0", 21, SandboxImages.Maven21),
        new(Maven, 3, "3.10.0", 25, SandboxImages.Maven25),
        new(Gradle, 7, "7.6.6", 8, SandboxImages.Gradle7Jdk8),
        new(Gradle, 7, "7.6.6", 11, SandboxImages.Gradle7Jdk11),
        new(Gradle, 7, "7.6.6", 17, SandboxImages.Gradle7Jdk17),
        new(Gradle, 8, "8.14.6", 8, SandboxImages.Gradle8Jdk8),
        new(Gradle, 8, "8.14.6", 11, SandboxImages.Gradle8Jdk11),
        new(Gradle, 8, "8.14.6", 17, SandboxImages.Gradle8Jdk17),
        new(Gradle, 8, "8.14.6", 21, SandboxImages.Gradle8Jdk21),
        new(Gradle, 9, "9.8.2", 17, SandboxImages.Gradle9Jdk17),
        new(Gradle, 9, "9.8.2", 21, SandboxImages.Gradle9Jdk21),
        new(Gradle, 9, "9.8.2", 25, SandboxImages.Gradle9Jdk25),
    ];

    private static readonly string[] GradleManifests = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];
    private static readonly string[] GradleBuilds = ["build.gradle", "build.gradle.kts"];

    [GeneratedRegex(@"^(?:1\.)?(\d{1,3})(?!\d)")]
    private static partial Regex JavaNumber();

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex PropertyReference();

    [GeneratedRegex(@"gradle-(\d{1,4}(?:\.\d{1,4}){0,3})(?:-[0-9A-Za-z.]+)*-(?:bin|all)\.zip")]
    private static partial Regex WrapperDistribution();

    [GeneratedRegex(@"JavaLanguageVersion\s*\.\s*of\s*\(\s*""?(\d{1,3})""?\s*\)|\bjvmToolchain\s*\(\s*(\d{1,3})\s*\)")]
    private static partial Regex Toolchain();

    [GeneratedRegex(@"\bsourceCompatibility\s*=\s*(?:JavaVersion\s*\.\s*VERSION_(\d{1,3}(?:_\d{1,3})?)|['""]?(\d{1,3}(?:\.\d{1,3})?)['""]?)")]
    private static partial Regex SourceCompatibility();

    [GeneratedRegex(@"\btargetCompatibility\s*=\s*(?:JavaVersion\s*\.\s*VERSION_(\d{1,3}(?:_\d{1,3})?)|['""]?(\d{1,3}(?:\.\d{1,3})?)['""]?)")]
    private static partial Regex TargetCompatibility();

    /// <summary>
    /// The Java stack, or null: with <paramref name="reason"/> untouched when no Java manifest is at the root, or set to
    /// the sentence naming the version asked for and the versions Scout runs.
    /// </summary>
    public static SandboxStack? Detect(string root, ref string reason)
    {
        if (SandboxStacks.Has(root, "pom.xml"))
        {
            return ForMaven(root, ref reason);
        }

        return GradleManifests.Any(file => SandboxStacks.Has(root, file)) ? ForGradle(root, ref reason) : null;
    }

    private static SandboxStack? ForMaven(string root, ref string reason)
    {
        var fromPom = MavenJava(Path.Combine(root, "pom.xml"));
        var file = fromPom is null ? VersionFile(root) : null;
        var declared = fromPom ?? file?.Java;
        var java = declared ?? DefaultJava;
        var image = Images.FirstOrDefault(i => i.Tool == Maven && i.Java == java);
        if (image is null)
        {
            reason = Unsupported(java);
            return null;
        }

        return new SandboxStack(
            SandboxStacks.Java,
            image.Image,
            MavenRestore,
            MavenTest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["MAVEN_OPTS"] = $"-Dmaven.repo.local=/work/cache/m2 -Duser.home={SandboxStacks.Home}",
                ["MAVEN_CONFIG"] = $"{SandboxStacks.Home}/.m2",
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            $"{Ran(declared, java)} with Maven {image.ToolVersion}.",
            declared is null ? [] : [new(fromPom is null ? file!.Value.File : "pom.xml", $"Java {declared}")],
            $"Java {java} with Maven {image.ToolVersion}");
    }

    private static SandboxStack? ForGradle(string root, ref string reason)
    {
        var wrapper = WrapperVersion(root);
        var major = wrapper is null ? DefaultGradle : int.Parse(wrapper.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        var line = Images.Where(i => i.Tool == Gradle && i.ToolMajor == major).OrderBy(i => i.Java).ToList();
        if (line.Count == 0)
        {
            reason = $"This project asks for Gradle {major}; Scout runs Gradle {List(Images.Where(i => i.Tool == Gradle).Select(i => i.ToolMajor))}.";
            return null;
        }

        var fromBuild = GradleJava(root);
        var file = fromBuild is null ? VersionFile(root) : null;
        var declared = fromBuild ?? file?.Java;
        if (declared is int asked && Images.All(i => i.Java != asked))
        {
            reason = Unsupported(asked);
            return null;
        }

        var image = declared is int wanted
            ? line.FirstOrDefault(i => i.Java == wanted)
            : line.FirstOrDefault(i => i.Java == DefaultJava) ?? line[^1];
        if (image is null)
        {
            reason = $"This project asks for Gradle {major} on Java {declared}; Scout runs Gradle {major} on Java {List(line.Select(i => i.Java))}.";
            return null;
        }

        var tool = wrapper is null ? "no Gradle version was declared." : $"the wrapper asks for Gradle {wrapper}.";
        return new SandboxStack(
            SandboxStacks.Java,
            image.Image,
            GradleRestore,
            GradleTest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["GRADLE_USER_HOME"] = "/work/cache/gradle",
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            $"{Ran(declared, image.Java)} with Gradle {image.ToolVersion} from the image, not the repository's wrapper; {tool}",
            [
                .. declared is null ? [] : new[] { new SandboxDeclaredVersion(fromBuild is null ? file!.Value.File : GradleBuilds.First(f => SandboxStacks.Has(root, f)), $"Java {declared}") },
                .. wrapper is null ? [] : new[] { new SandboxDeclaredVersion("gradle/wrapper/gradle-wrapper.properties", $"Gradle {wrapper}") },
            ],
            $"Java {image.Java} with Gradle {image.ToolVersion}");
    }

    private static string Ran(int? declared, int java) =>
        declared is null ? $"No Java version was declared; ran on Java {java}" : $"Ran on Java {java}";

    private static string Unsupported(int java) =>
        $"This project asks for Java {java}; Scout runs Java {List(Images.Select(i => i.Java))}.";

    private static string List(IEnumerable<int> values)
    {
        var distinct = values.Distinct().Order().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return distinct.Count == 1 ? distinct[0] : $"{string.Join(", ", distinct[..^1])} and {distinct[^1]}";
    }

    private static int? ParseJava(string? value)
    {
        var match = value is null ? null : JavaNumber().Match(value.Trim());
        return match is { Success: true } ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static int? MavenJava(string pom)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(pom, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var project = document.Root;
        if (project is null)
        {
            return null;
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in Children(project, "properties").SelectMany(p => p.Elements()))
        {
            properties.TryAdd(property.Name.LocalName, property.Value.Trim());
        }

        string? Resolve(string? value)
        {
            var reference = value is null ? null : PropertyReference().Match(value);
            return reference is { Success: true } ? properties.GetValueOrDefault(reference.Groups[1].Value) : value;
        }

        int? Property(string name) => ParseJava(Resolve(properties.GetValueOrDefault(name)));

        var pluginRelease = Children(project, "build")
            .SelectMany(build => build.Descendants().Where(e => e.Name.LocalName == "plugin"))
            .Where(plugin => Children(plugin, "artifactId").Any(id => id.Value.Trim() == "maven-compiler-plugin"))
            .SelectMany(plugin => Children(plugin, "configuration").SelectMany(c => Children(c, "release")))
            .Select(release => ParseJava(Resolve(release.Value.Trim())))
            .FirstOrDefault(java => java is not null);

        return Property("maven.compiler.release")
            ?? Property("maven.compiler.source")
            ?? Property("maven.compiler.target")
            ?? pluginRelease
            ?? Property("java.version");
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName == name);

    private static int? GradleJava(string root)
    {
        var text = string.Concat(GradleBuilds.Where(file => SandboxStacks.Has(root, file)).Select(file => File.ReadAllText(Path.Combine(root, file)) + "\n"));
        return Compatibility(Toolchain().Match(text))
            ?? Compatibility(SourceCompatibility().Match(text))
            ?? Compatibility(TargetCompatibility().Match(text));
    }

    private static int? Compatibility(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var group = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
        return ParseJava(group.Value.Replace('_', '.'));
    }

    private static (int Java, string File)? VersionFile(string root)
    {
        if (SandboxStacks.Has(root, ".java-version")
            && File.ReadLines(Path.Combine(root, ".java-version")).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line
            && VersionToken(line) is int fromJavaVersion)
        {
            return (fromJavaVersion, ".java-version");
        }

        if (!SandboxStacks.Has(root, ".sdkmanrc"))
        {
            return null;
        }

        var sdk = File.ReadLines(Path.Combine(root, ".sdkmanrc")).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("java=", StringComparison.Ordinal));
        return sdk is not null && VersionToken(sdk["java=".Length..]) is int fromSdkman ? (fromSdkman, ".sdkmanrc") : null;
    }

    private static int? VersionToken(string value) =>
        ParseJava(value.Split('-').FirstOrDefault(token => token.Length > 0 && char.IsAsciiDigit(token[0])));

    private static string? WrapperVersion(string root)
    {
        var properties = Path.Combine(root, "gradle", "wrapper", "gradle-wrapper.properties");
        if (!File.Exists(properties))
        {
            return null;
        }

        return File.ReadLines(properties)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("distributionUrl", StringComparison.Ordinal))
            .Select(l => WrapperDistribution().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();
    }
}
