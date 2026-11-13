using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Meetings;

/// <summary>
/// DI composition for kept meetings. Registers <see cref="SqliteMeetingStore"/> as the <see cref="IMeetingStore"/>, and
/// <see cref="MeetingMarkdown.Render"/> as the <c>Func&lt;MeetingRecord, string&gt;</c> that exports one, only when
/// <c>Scout:Meetings:DbPath</c> names a file whose folder exists; otherwise registers nothing and the feature is off.
/// </summary>
public static class MeetingsServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="AddMeetings"/>.</summary>
    public const string ConfigSection = "Scout:Meetings";

    /// <summary>Registers the meetings store when the configured database's folder exists.</summary>
    public static IServiceCollection AddMeetings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var dbPath = configuration.GetSection(ConfigSection)["DbPath"];
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            return services;
        }

        var fullPath = Path.GetFullPath(dbPath);
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
        {
            return services;
        }

        services.AddSingleton<IMeetingStore>(_ => new SqliteMeetingStore(fullPath));
        services.AddSingleton<Func<MeetingRecord, string>>(MeetingMarkdown.Render);
        return services;
    }
}
