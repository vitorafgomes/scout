using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Scout.Contracts;
using Scout.Plugin.SelfInfo;

namespace Scout.Plugin.SelfInfo.Tests;

/// <summary>
/// SelfInfo Path B disclosure gating. The identity gates OUTPUT DISCLOSURE only:
/// with no identity, no admins, a non-admin caller, or an admin outside the DM
/// constraint, the plugin must return the exact public-safe output (fail-closed).
/// An admin who satisfies the constraint receives the survey's "medium" tier
/// (real paths, machine name, exact versions/counts, project names) but never the
/// "high/never" tier (secrets, hostname/FQDN, IPs, endpoints, OS build/kernel),
/// which stays redacted unconditionally.
/// </summary>
public sealed class SelfInfoDisclosureTierTests
{
    private const string DiscordSecret = "SECRET-abc123";
    private const string GitHubSecret = "ghp_FAKE";
    private const string AdminId = "admin-1";
    private const string NonAdminId = "user-2";
    private const string ProjectName = "scout-repo";

    // Four dotted octets = network identity. Scout's own semver and the exact
    // .NET version are capped at three segments, so neither matches.
    private static readonly Regex IpLikePattern =
        new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.CultureInvariant);

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Chat:Model"] = "qwen2.5",
                ["Scout:Adapters:Discord:BotToken"] = DiscordSecret,
                ["Scout:Plugins:GitHub:Token"] = GitHubSecret,
                ["Scout:Projects:Items:0:Name"] = ProjectName,
                ["Scout:Projects:Items:0:RepoPath"] = "/home/someone/repos/scout",
            })
            .Build();

    private static SelfInfoOptions Options(bool dmOnly, params string[] admins) =>
        new() { Enabled = true, DmOnly = dmOnly, AdminUserIds = admins };

    private static ScoutRequestIdentity Identity(string userId, bool isDm) =>
        new(userId, "channel-1", isDm, "Discord");

    // Runs every function under the given options and identity (null = no scope),
    // returning the concatenated output. The scope stays open across the awaits so
    // the plugin observes the identity at call time.
    private static async Task<string> InvokeAll(SelfInfoOptions options, ScoutRequestIdentity? identity)
    {
        var context = new AsyncLocalScoutRequestContext();
        var plugin = new SelfInfoPlugin(Config(), TimeProvider.System, context, options);
        var functions = new Func<CancellationToken, Task<string>>[]
        {
            plugin.WhereInstalledAsync,
            plugin.SystemInfoAsync,
            plugin.ResourceUsageAsync,
            plugin.ActiveConfigurationAsync,
            plugin.WorkerStatusAsync,
        };

        var builder = new StringBuilder();
        IDisposable? scope = identity is null ? null : context.BeginScope(identity);
        try
        {
            foreach (var function in functions)
            {
                builder.AppendLine(await function(CancellationToken.None));
            }
        }
        finally
        {
            scope?.Dispose();
        }

        return builder.ToString();
    }

    private static async Task<string> InvokeSystemInfo(SelfInfoOptions options, ScoutRequestIdentity identity)
    {
        var context = new AsyncLocalScoutRequestContext();
        var plugin = new SelfInfoPlugin(Config(), TimeProvider.System, context, options);
        using (context.BeginScope(identity))
        {
            return await plugin.SystemInfoAsync(CancellationToken.None);
        }
    }

    private static async Task<string> InvokeResourceUsage(SelfInfoOptions options, ScoutRequestIdentity identity)
    {
        var context = new AsyncLocalScoutRequestContext();
        var plugin = new SelfInfoPlugin(Config(), TimeProvider.System, context, options);
        using (context.BeginScope(identity))
        {
            return await plugin.ResourceUsageAsync(CancellationToken.None);
        }
    }

    // Markers that only the elevated (medium) tier emits.
    private static void ShouldBeElevated(string output)
    {
        output.ShouldContain(AppContext.BaseDirectory);        // real install path
        output.ShouldContain(Environment.MachineName);          // machine name (medium)
        output.ShouldContain(ProjectName);                      // knowledge-project name
    }

    private static void ShouldBePublic(string output)
    {
        output.ShouldNotContain(AppContext.BaseDirectory);
        output.ShouldNotContain(ProjectName);
        // MachineName is only revealed at the elevated tier; empty machine name is
        // never asserted (it cannot leak an empty string).
        if (!string.IsNullOrEmpty(Environment.MachineName))
        {
            output.ShouldNotContain(Environment.MachineName);
        }
    }

    // The "high/never" tier must stay redacted regardless of tier: never a secret
    // value, never an OS build/kernel string (the full OS description), never an
    // endpoint. Machine name is medium (allowed on elevated) and is NOT checked
    // here; the survey separates the short machine name from a resolvable hostname.
    private static void ShouldNeverLeakHighTier(string output)
    {
        output.ShouldNotContain(DiscordSecret);
        output.ShouldNotContain(GitHubSecret);

        // OS build/kernel: the full OS description carries the kernel/build on Linux
        // and must never appear (only the OS family "Linux/Windows/..." may).
        var osDescription = RuntimeInformation.OSDescription;
        if (!string.IsNullOrWhiteSpace(osDescription))
        {
            output.ShouldNotContain(osDescription);
        }
    }

    [Fact]
    public async Task No_Identity_Even_With_Admins_Configured_Is_Public()
    {
        // Fail-closed: the direct-chat/host path sets no identity, so admins never
        // elevate it.
        var output = await InvokeAll(Options(dmOnly: false, AdminId), identity: null);

        ShouldBePublic(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task Admin_In_Dm_With_DmOnly_Is_Elevated_Medium_But_Never_HighTier()
    {
        var output = await InvokeAll(Options(dmOnly: true, AdminId), Identity(AdminId, isDm: true));

        ShouldBeElevated(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task Admin_In_Channel_With_DmOnly_Is_Public()
    {
        // DmOnly is an AND-constraint: the same admin outside a DM is not elevated.
        var output = await InvokeAll(Options(dmOnly: true, AdminId), Identity(AdminId, isDm: false));

        ShouldBePublic(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task Admin_In_Channel_With_DmOnly_False_Is_Elevated()
    {
        var output = await InvokeAll(Options(dmOnly: false, AdminId), Identity(AdminId, isDm: false));

        ShouldBeElevated(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task NonAdmin_In_Dm_Is_Public()
    {
        var output = await InvokeAll(Options(dmOnly: false, AdminId), Identity(NonAdminId, isDm: true));

        ShouldBePublic(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task Empty_AdminUserIds_Even_When_Enabled_Is_Public()
    {
        // Enabled but no admins configured: nobody is elevated.
        var output = await InvokeAll(Options(dmOnly: false), Identity(AdminId, isDm: true));

        ShouldBePublic(output);
        ShouldNeverLeakHighTier(output);
    }

    [Fact]
    public async Task Elevated_Exact_Version_Never_Resembles_An_Ip()
    {
        // system_info emits the exact .NET version at the elevated tier; it must be
        // capped so it never matches a four-octet address.
        var output = await InvokeSystemInfo(Options(dmOnly: false, AdminId), Identity(AdminId, isDm: false));

        IpLikePattern.IsMatch(output).ShouldBeFalse($"IP-like pattern leaked in: {output}");
    }

    [Fact]
    public async Task Elevated_Exact_Memory_Never_Resembles_An_Ip()
    {
        // resource_usage emits an exact byte count at the elevated tier; the
        // invariant-culture grouping must never produce a dotted-decimal IP shape.
        var output = await InvokeResourceUsage(Options(dmOnly: false, AdminId), Identity(AdminId, isDm: false));

        IpLikePattern.IsMatch(output).ShouldBeFalse($"IP-like pattern leaked in: {output}");
    }

    [Fact]
    public async Task Elevated_Where_Installed_Extends_The_Public_Output()
    {
        // The public output is byte-for-byte today's output; the elevated tier only
        // appends medium lines, so the public string is a prefix of the elevated one.
        var context = new AsyncLocalScoutRequestContext();
        var plugin = new SelfInfoPlugin(
            Config(), TimeProvider.System, context, Options(dmOnly: false, AdminId));

        var publicOutput = await plugin.WhereInstalledAsync(CancellationToken.None);

        string elevatedOutput;
        using (context.BeginScope(Identity(AdminId, isDm: false)))
        {
            elevatedOutput = await plugin.WhereInstalledAsync(CancellationToken.None);
        }

        elevatedOutput.ShouldStartWith(publicOutput);
        elevatedOutput.Length.ShouldBeGreaterThan(publicOutput.Length);
    }

    [Fact]
    public void Resolve_Fails_Closed_For_Every_Non_Elevating_Combination()
    {
        var context = new AsyncLocalScoutRequestContext();

        // No identity -> Public.
        Plugin(context, Options(dmOnly: false, AdminId)).Resolve().ShouldBe(DisclosureTier.Public);

        // Non-admin in a DM -> Public.
        using (context.BeginScope(Identity(NonAdminId, isDm: true)))
        {
            Plugin(context, Options(dmOnly: false, AdminId)).Resolve().ShouldBe(DisclosureTier.Public);
        }

        // Admin in a channel while DmOnly -> Public.
        using (context.BeginScope(Identity(AdminId, isDm: false)))
        {
            Plugin(context, Options(dmOnly: true, AdminId)).Resolve().ShouldBe(DisclosureTier.Public);
        }

        // Admin in a DM while DmOnly -> Elevated.
        using (context.BeginScope(Identity(AdminId, isDm: true)))
        {
            Plugin(context, Options(dmOnly: true, AdminId)).Resolve().ShouldBe(DisclosureTier.Elevated);
        }
    }

    private static SelfInfoPlugin Plugin(IScoutRequestContext context, SelfInfoOptions options) =>
        new(Config(), TimeProvider.System, context, options);
}
