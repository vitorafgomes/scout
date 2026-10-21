using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Scout.Contracts;

namespace Scout.Plugin.SelfInfo;

/// <summary>
/// Read-only self/host diagnostics plugin exposed to the LLM as Semantic Kernel
/// functions. It answers "where and how is Scout installed" using only local
/// .NET/OS APIs and a fixed whitelist of non-secret configuration; it opens no
/// network connections and reads no credentials.
///
/// Disclosure is tiered (SelfInfo Path B). Each function first builds the
/// public-safe output that every workspace member may read, then — only when the
/// caller resolves to <see cref="DisclosureTier.Elevated"/> — appends the survey's
/// "medium" tier: real install/config paths, machine name, exact versions/counts,
/// and knowledge-project names. Elevation is derived from the ambient
/// <see cref="IScoutRequestContext"/> (an admin user id, optionally constrained to
/// a direct message); it gates OUTPUT DISCLOSURE only and never authorizes a write.
///
/// Security invariant: the public-safe branch is byte-for-byte what the plugin
/// emitted before Path B, so it is safe assuming the least-privileged member reads
/// it. The survey's "high/never" tier is redacted UNCONDITIONALLY — even for an
/// admin in a DM the plugin never emits secret values or their presence, a
/// hostname, IPs/MACs/interfaces, endpoint URLs/ports, or OS build/kernel numbers.
/// Fields that cannot be produced in redacted form are omitted. Every call returns
/// a non-empty string and never throws back into the kernel.
/// </summary>
public sealed class SelfInfoPlugin
{
    private const long OneGigabyte = 1024L * 1024 * 1024;
    private const long OneMegabyte = 1024L * 1024;

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly IScoutRequestContext _requestContext;
    private readonly SelfInfoOptions _options;
    private readonly DateTimeOffset _startedAt;

    /// <summary>Creates the plugin over host configuration, a clock, the ambient request context, and options.</summary>
    /// <param name="configuration">Host configuration. Only whitelisted, non-secret keys are read.</param>
    /// <param name="timeProvider">Clock used to derive a coarse uptime bucket.</param>
    /// <param name="requestContext">Ambient caller identity read at call time to resolve the disclosure tier.</param>
    /// <param name="options">Admin allow-list and DM-only constraint that gate the elevated tier.</param>
    public SelfInfoPlugin(
        IConfiguration configuration,
        TimeProvider timeProvider,
        IScoutRequestContext requestContext,
        SelfInfoOptions options)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _requestContext = requestContext ?? throw new ArgumentNullException(nameof(requestContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _startedAt = _clock.GetUtcNow();
    }

    /// <summary>Describe where Scout is installed in logical terms only.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A redacted, path-free description of the install kind, config location, and a coarse free-space bucket. Elevated callers also see the real install and config paths.</returns>
    [KernelFunction("where_installed")]
    [Description("Describe where Scout is installed in logical terms only: whether it runs from an AppImage mount or a standard install, that configuration lives in the standard per-user location, and a coarse install-drive free-space bucket. No absolute paths.")]
    public Task<string> WhereInstalledAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var lines = new List<string>
            {
                IsAppImageMount()
                    ? "Scout appears to be running from an AppImage mount."
                    : "Scout appears to be running from a standard install.",
                "Configuration is loaded from the standard per-user application configuration location.",
            };

            var disk = TryDiskBucket();
            if (disk is not null)
            {
                lines.Add($"Install drive free space: {disk}.");
            }

            // Elevated (survey "medium"): real install/config paths, which embed the
            // OS username. Never a hostname/IP/endpoint ("high/never").
            if (Resolve() == DisclosureTier.Elevated)
            {
                lines.Add($"Install root: {AppContext.BaseDirectory}");
                var configDir = ConfigDirectory();
                if (!string.IsNullOrEmpty(configDir))
                {
                    lines.Add($"Configuration directory: {configDir}");
                }
            }

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Report coarse system information with no host or version detail.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>OS family, architectures, .NET major, RID family, CPU count, time zone, and culture. Elevated callers also see the machine name and exact .NET version. No hostname, no OS build.</returns>
    [KernelFunction("system_info")]
    [Description("Report coarse system information: OS family/name (no build number), OS and process architecture, .NET major version, runtime identifier family, logical processor count, system time zone, and culture. No hostname, no username.")]
    public Task<string> SystemInfoAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var lines = new List<string>
            {
                $"Operating system: {OsFamily()}",
                $"OS architecture: {RuntimeInformation.OSArchitecture}",
                $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
                $".NET runtime: .NET {Environment.Version.Major}",
                $"Runtime identifier family: {RidFamily()}",
                $"Logical processors: {Environment.ProcessorCount}",
                $"System time zone: {TimeZoneInfo.Local.Id}",
                $"Culture: {CurrentCulture()}",
            };

            // Elevated (survey "medium"): machine name and the exact .NET version.
            // The machine name is medium; a hostname/OS build/kernel is "high/never"
            // and stays redacted. The version is capped at three numeric segments so
            // it can never resemble a four-octet network address.
            if (Resolve() == DisclosureTier.Elevated)
            {
                lines.Add($"Machine name: {Environment.MachineName}");
                lines.Add($".NET runtime (exact): .NET {Environment.Version.ToString(3)}");
            }

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Report coarse resource health only.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Qualitative uptime and managed-memory buckets. Elevated callers also see exact uptime and managed-memory counts. No process id.</returns>
    [KernelFunction("resource_usage")]
    [Description("Report coarse resource health of this worker: a qualitative uptime bucket and a qualitative managed-memory bucket. No process id, no exact byte or second counts, no other processes.")]
    public Task<string> ResourceUsageAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var lines = new List<string>
            {
                "Worker health: running.",
                $"Uptime: {UptimeBucket()}.",
                $"Managed memory: {MemoryBucket()}.",
            };

            // Elevated (survey "medium"): exact uptime and managed-memory counts.
            // Still no PID ("process internals" stay omitted). Memory is formatted
            // with the invariant culture's comma grouping so no dotted-decimal group
            // can ever resemble a four-octet network address.
            if (Resolve() == DisclosureTier.Elevated)
            {
                var uptime = _clock.GetUtcNow() - _startedAt;
                lines.Add(
                    $"Uptime (exact): {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s.");
                var bytes = GC.GetTotalMemory(forceFullCollection: false);
                lines.Add(
                    $"Managed memory (exact): {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.");
            }

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Report only public-safe capability names that are configured.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>LLM provider and chat model names, and configured chat adapter names. Elevated callers also see configured knowledge-project names. No endpoints, no tokens, no secret presence.</returns>
    [KernelFunction("active_configuration")]
    [Description("Report only public-safe capability names that are configured: the LLM provider name and chat model name, and which chat adapters (Discord/Slack/Teams) are configured, by name only. No endpoint URLs, no tokens, no secret presence flags.")]
    public Task<string> ActiveConfigurationAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Provider and model names are non-secret and safe to name. Endpoint
            // URLs are never read or emitted.
            var provider = _configuration["Scout:Llm:Provider"];
            var model = _configuration["Scout:Llm:Ollama:Chat:Model"];

            var adapters = ConfiguredAdapters();

            var lines = new List<string>
            {
                $"LLM provider: {NameOrUnset(provider)}.",
                $"Chat model: {NameOrUnset(model)}.",
                adapters.Count == 0
                    ? "Chat adapters configured: none."
                    : $"Chat adapters configured: {string.Join(", ", adapters)}.",
            };

            // Elevated (survey "medium"): knowledge-project names, a richer view of
            // the active configuration. Still names only — never endpoint URLs and
            // never any token value or presence flag ("high/never").
            if (Resolve() == DisclosureTier.Elevated)
            {
                var projects = ConfiguredProjectNames();
                lines.Add(projects.Count == 0
                    ? "Knowledge projects configured: none."
                    : $"Knowledge projects configured: {string.Join(", ", projects)}.");
            }

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>Report coarse worker status and Scout's own version.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Qualitative worker status, hosting environment name, and Scout's own version. Elevated callers also see the machine name. No process id, no paths.</returns>
    [KernelFunction("worker_status")]
    [Description("Report coarse worker status: that the worker is running, the hosting environment name (e.g. Production/Development), and Scout's own version. No process id, no paths.")]
    public Task<string> WorkerStatusAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var lines = new List<string>
            {
                "Worker status: running.",
                $"Hosting environment: {HostingEnvironment()}.",
                $"Scout version: {ScoutVersion()}.",
            };

            // Elevated (survey "medium"): the machine the worker runs on. Never a
            // hostname/IP/endpoint ("high/never").
            if (Resolve() == DisclosureTier.Elevated)
            {
                lines.Add($"Worker host machine: {Environment.MachineName}.");
            }

            return Task.FromResult(string.Join("\n", lines));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return Task.FromResult(ErrorLine(ex));
        }
    }

    /// <summary>
    /// Resolve the disclosure tier for the current caller. Fail-closed at every
    /// step: any missing signal yields <see cref="DisclosureTier.Public"/>.
    /// </summary>
    /// <remarks>
    /// Order: no ambient identity -> Public (direct-chat/host paths). No admins
    /// configured -> Public. Caller not in the ordinal admin allow-list -> Public.
    /// <see cref="SelfInfoOptions.DmOnly"/> set but the message is not a direct
    /// message -> Public (DmOnly is an AND-constraint on admin, never an
    /// independent unlock). Otherwise -> Elevated.
    /// </remarks>
    internal DisclosureTier Resolve()
    {
        var identity = _requestContext.Current;
        if (identity is null)
        {
            return DisclosureTier.Public;
        }

        if (_options.AdminUserIds.Count == 0)
        {
            return DisclosureTier.Public;
        }

        if (!IsAdmin(identity.UserId))
        {
            return DisclosureTier.Public;
        }

        if (_options.DmOnly && !identity.IsDirectMessage)
        {
            return DisclosureTier.Public;
        }

        return DisclosureTier.Elevated;
    }

    private bool IsAdmin(string userId)
    {
        foreach (var adminId in _options.AdminUserIds)
        {
            if (string.Equals(adminId, userId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // AppImage detection is a boolean over the base directory prefix; the path
    // itself is never emitted at the public tier.
    private static bool IsAppImageMount()
        => AppContext.BaseDirectory.StartsWith("/tmp/.mount_", StringComparison.Ordinal);

    // Real per-user configuration directory, emitted only at the elevated tier.
    // {LocalAppData}/Scout mirrors the survey's config-location note; it embeds the
    // OS username, which is "medium" and therefore admin/DM-gated.
    private static string ConfigDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData)
            ? string.Empty
            : Path.Combine(localAppData, "Scout");
    }

    // Reads free space on the install drive and returns only a coarse bucket.
    // The drive letter/root path is never emitted. Fails closed to null.
    private static string? TryDiskBucket()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            var free = drive.AvailableFreeSpace;
            return free switch
            {
                > 10 * OneGigabyte => "plenty (>10 GB free)",
                >= 2 * OneGigabyte => "moderate (2-10 GB free)",
                _ => "low (<2 GB free)",
            };
        }
        catch (Exception ex) when (IsHandledFailure(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    private static string OsFamily()
        => OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsFreeBSD() ? "FreeBSD"
        : "Unknown";

    // Coarse RID family built from OS token and architecture only, so no distro
    // or OS version can leak through the raw runtime identifier.
    private static string RidFamily()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }

    private static string CurrentCulture()
    {
        var name = CultureInfo.CurrentCulture.Name;
        return string.IsNullOrEmpty(name) ? "invariant culture" : name;
    }

    private string UptimeBucket()
    {
        var uptime = _clock.GetUtcNow() - _startedAt;
        if (uptime < TimeSpan.FromHours(1))
        {
            return "less than an hour";
        }

        if (uptime < TimeSpan.FromDays(1))
        {
            return "up for a few hours";
        }

        return uptime < TimeSpan.FromDays(7) ? "up for a few days" : "up for over a week";
    }

    private static string MemoryBucket()
    {
        var bytes = GC.GetTotalMemory(forceFullCollection: false);
        if (bytes < 256 * OneMegabyte)
        {
            return "light (a small footprint)";
        }

        return bytes < OneGigabyte ? "moderate (a few hundred MB)" : "heavy (over ~1 GB)";
    }

    // Presence check only, mirroring the host startup marker logic. Each marker
    // value is a secret (bot token) or opaque identifier and is never read into
    // output: only the adapter name is emitted when the marker is populated.
    private IReadOnlyList<string> ConfiguredAdapters()
    {
        var adapters = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configuration["Scout:Adapters:Discord:BotToken"]))
        {
            adapters.Add("Discord");
        }

        if (!string.IsNullOrWhiteSpace(_configuration["Scout:Adapters:Slack:BotToken"]))
        {
            adapters.Add("Slack");
        }

        if (!string.IsNullOrWhiteSpace(_configuration["Scout:Adapters:Teams:AppId"]))
        {
            adapters.Add("Teams");
        }

        return adapters;
    }

    // Project names are non-secret capability labels ("medium"), emitted only at
    // the elevated tier. Only the Name is read; repo paths and any other project
    // field stay unread.
    private IReadOnlyList<string> ConfiguredProjectNames()
    {
        var names = new List<string>();
        foreach (var child in _configuration.GetSection("Scout:Projects:Items").GetChildren())
        {
            var name = child["Name"];
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private string HostingEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? _configuration["environment"];
        return string.IsNullOrWhiteSpace(env) ? "Production" : env;
    }

    // Scout's own semantic version is allowed. Emitted with at most three
    // numeric segments so it can never resemble a network address.
    private static string ScoutVersion()
    {
        var version = typeof(SelfInfoPlugin).Assembly.GetName().Version;
        return version is null ? "unknown" : version.ToString(3);
    }

    private static string NameOrUnset(string? value)
        => string.IsNullOrWhiteSpace(value) ? "not configured" : value;

    private static bool IsHandledFailure(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or InvalidOperationException
            or NotSupportedException
            or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "Request cancelled.",
        _ => "Could not read local host information.",
    };
}
