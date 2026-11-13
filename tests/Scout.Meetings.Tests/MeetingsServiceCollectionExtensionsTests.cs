using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Meetings.Tests;

public sealed class MeetingsServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("no path", false)]
    [InlineData("a missing folder", false)]
    [InlineData("an existing folder", true)]
    public void AddMeetings_Registers_The_Store_Only_With_A_DbPath_Whose_Folder_Exists(string dbPath, bool registered)
    {
        var folder = Directory.CreateTempSubdirectory("scout-meetings-di-").FullName;
        try
        {
            var path = dbPath switch
            {
                "no path" => null,
                "a missing folder" => Path.Combine(folder, "missing", "meetings.db"),
                _ => Path.Combine(folder, "meetings.db"),
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Meetings:DbPath"] = path })
                .Build();

            using var provider = new ServiceCollection().AddMeetings(configuration).BuildServiceProvider();
            var store = provider.GetService<IMeetingStore>();

            (store is SqliteMeetingStore).ShouldBe(registered, $"with {dbPath} the meetings store is {(registered ? "on" : "off")}");
            var render = provider.GetService<Func<MeetingRecord, string>>();
            (render is not null).ShouldBe(registered, "the Markdown export is on exactly when the store is");
            var meeting = new MeetingRecord(null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Sync", [], []);
            (render?.Invoke(meeting)).ShouldBe(registered ? MeetingMarkdown.Render(meeting) : null);
            File.Exists(Path.Combine(folder, "meetings.db")).ShouldBeFalse("resolving the store opens no database");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
