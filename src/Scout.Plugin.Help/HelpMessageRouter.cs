using System.Reflection;
using Scout.Core.Chat;
using Scout.Core.Plugins;

namespace Scout.Plugin.Help;

/// <summary>
/// Keyword router for the Help plugin. Matches only on the full trimmed
/// lowercase content of the inbound message, so <c>@scout pinging</c> does
/// not trigger <c>ping</c>. Returns <c>null</c> for anything that does not
/// match exactly, letting downstream routers have their turn.
///
/// The plugin list rendered by <c>help</c> is resolved from DI and must
/// never include secrets or plugin configuration — only the plugin names
/// reach the reply.
/// </summary>
public sealed class HelpMessageRouter : IMessageRouter
{
    private readonly IEnumerable<IScoutPlugin> _plugins;
    private readonly string _workerVersion;

    public HelpMessageRouter(IEnumerable<IScoutPlugin> plugins)
        : this(plugins, workerVersion: null)
    {
    }

    // Overload kept internal for tests: lets a test pin the version string
    // instead of depending on the executing assembly attribute.
    internal HelpMessageRouter(IEnumerable<IScoutPlugin> plugins, string? workerVersion)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _workerVersion = string.IsNullOrWhiteSpace(workerVersion)
            ? ReadAssemblyVersion()
            : workerVersion!;
    }

    /// <inheritdoc />
    public Task<ChatResponse?> RouteAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = (message.Text ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult<ChatResponse?>(null);
        }

        // Exact match on the full trimmed, lowercase content.
        var token = normalized.ToLowerInvariant();

        ChatResponse? response = token switch
        {
            "ping" => ChatResponse.Plain("pong"),
            "help" => ChatResponse.Plain(BuildHelpText()),
            "version" => ChatResponse.Plain($"Scout worker v{_workerVersion}"),
            _ => null,
        };

        return Task.FromResult(response);
    }

    private string BuildHelpText()
    {
        var builtIns = new[]
        {
            "- @scout ping    - heartbeat reply",
            "- @scout help    - this message",
            "- @scout version - worker version",
        };

        // Any plugin other than Help itself is surfaced as a bullet. Only
        // the plugin name is exposed - never its config or capabilities.
        var others = _plugins
            .Where(p => p is not null)
            .Where(p => !string.Equals(p.Name, HelpPlugin.PluginName, StringComparison.Ordinal))
            .Select(p => $"- {p.Name}")
            .ToArray();

        // Wrap the command list in a triple-backtick code block so Discord
        // renders it with a monospace font and the dash columns line up.
        var lines = new List<string>(capacity: builtIns.Length + others.Length + 4)
        {
            "Scout is online. Commands:",
            "```",
        };
        lines.AddRange(builtIns);
        lines.AddRange(others);
        lines.Add("```");

        return string.Join('\n', lines);
    }

    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(HelpMessageRouter).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip SourceLink suffix ("1.0.0+abcdef") so the reply stays
            // compact in chat.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "0.0.0";
    }
}
