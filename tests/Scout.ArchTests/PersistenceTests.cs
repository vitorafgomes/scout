using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Persistence stays behind one store per use case: only the store implementations, their Entity Framework model and
/// migrations, and the project knowledge store on its vector connector reference SQLite or Entity Framework, and no other
/// source file holds a SQL statement.
/// </summary>
public class PersistenceTests
{
    private static readonly string[] DatabaseNamespaces = ["Microsoft.Data.Sqlite", "Microsoft.EntityFrameworkCore"];

    private static readonly string[] StoreTypes =
    [
        "Scout.Audit.SqliteAuditStore",
        "Scout.Audit.Storage.",
        "Scout.Meetings.SqliteMeetingStore",
        "Scout.Meetings.Storage.",
        "Scout.Knowledge.SqliteProjectKnowledgeStore",
    ];

    private static readonly string[] StoreFiles =
    [
        "src/Scout.Audit/SqliteAuditStore.cs",
        "src/Scout.Audit/Storage/",
        "src/Scout.Meetings/SqliteMeetingStore.cs",
        "src/Scout.Meetings/Storage/",
        "src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs",
    ];

    private static readonly string[] SqlStatements = ["SELECT ", "INSERT INTO", "UPDATE ", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "PRAGMA "];

    private static readonly Assembly[] Libraries =
    [
        typeof(Scout.Llm.LlmServiceCollectionExtensions).Assembly,
        typeof(Scout.Skills.SkillSelector).Assembly,
        typeof(Scout.Knowledge.ProjectKnowledgeService).Assembly,
        typeof(Scout.Conversation.OriginNotifier).Assembly,
        typeof(Scout.Adapter.Discord.DiscordChatAdapter).Assembly,
        typeof(Scout.Adapter.Slack.SlackChatAdapter).Assembly,
        typeof(Scout.Adapter.Teams.InboundTeamsMessage).Assembly,
        typeof(Scout.Adapter.Local.LocalChatAdapter).Assembly,
        typeof(Scout.Plugin.GitHub.GitHubOptions).Assembly,
        typeof(Scout.Plugin.Kubernetes.KubernetesOptions).Assembly,
        typeof(Scout.Plugin.YouTube.YouTubeOptions).Assembly,
        typeof(Scout.Transcription.Whisper.WhisperAudioTranscriber).Assembly,
        typeof(Scout.Meetings.SqliteMeetingStore).Assembly,
        typeof(Scout.Sandbox.DockerSandboxRunner).Assembly,
        typeof(Scout.Operator.OperatorComments).Assembly,
        typeof(Scout.Audit.IRepositoryAudits).Assembly,
        typeof(Scout.ControlPlane.Features.FeatureCatalog).Assembly,
    ];

    /// <summary>
    /// The control plane's file stores, kept as JSON files outside Entity Framework by the amendment of 2026-11-10 to the
    /// persistence decision, by name; a new one is a decision, not a quiet addition.
    /// </summary>
    private static readonly string[] FileStores =
    [
        "Scout.ControlPlane.Actions.JsonFileActionQueue",
        "Scout.ControlPlane.Actions.JsonLinesWriteLog",
        "Scout.ControlPlane.Controls.JsonFileOwnerControlQueue",
        "Scout.ControlPlane.Features.JsonFileFeatureStateStore",
        "Scout.ControlPlane.Operator.JsonFileOperatorStore",
        "Scout.ControlPlane.Providers.JsonFileLocalRuntimeConfigStore",
    ];

    internal static IEnumerable<string> FileStoresIn(IEnumerable<Type> types) =>
        types.Where(type => type is { IsClass: true, IsAbstract: false } && (type.Name.StartsWith("JsonFile", StringComparison.Ordinal) || type.Name.StartsWith("JsonLines", StringComparison.Ordinal)))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal);

    internal static bool IsStoreType(string fullName) => StoreTypes.Any(store => fullName.StartsWith(store, StringComparison.Ordinal));

    internal static bool IsStoreFile(string relative) => StoreFiles.Any(store => relative.StartsWith(store, StringComparison.Ordinal));

    internal static IEnumerable<string> SqlIn(string source) =>
        SqlStatements.Where(statement => source.Contains(statement, StringComparison.Ordinal));

    [Fact]
    public void The_file_stores_are_exactly_the_ones_the_persistence_decision_scopes_out()
    {
        FileStoresIn(Libraries.SelectMany(assembly => assembly.GetTypes())).ShouldBe(FileStores.Order(StringComparer.Ordinal), "a new file store needs the persistence decision first: a store behind Entity Framework, or an amendment that names it");
    }

    [Fact]
    public void The_file_store_scan_sees_a_fourth_one()
    {
        FileStoresIn([typeof(JsonFileFakeStore), typeof(Scout.ControlPlane.Actions.JsonFileActionQueue)])
            .ShouldBe(["Scout.ArchTests.PersistenceTests+JsonFileFakeStore", "Scout.ControlPlane.Actions.JsonFileActionQueue"]);
    }

    [Fact]
    public void Only_the_stores_reference_sqlite_or_entity_framework()
    {
        var offenders = Libraries
            .SelectMany(assembly => Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(DatabaseNamespaces).GetResult().FailingTypeNames ?? [])
            .Select(name => name.Replace('/', '.'))
            .Where(name => !IsStoreType(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("a type outside the stores reaches the database; give the use case a store and keep SQLite and Entity Framework inside it");
    }

    [Fact]
    public void No_source_file_outside_the_stores_holds_a_sql_statement()
    {
        var offenders = SourceText.CSharpUnder("src")
            .Select(file => (File: SourceText.RepoPath(file), Sql: SqlIn(File.ReadAllText(file)).ToList()))
            .Where(found => found.Sql.Count > 0 && !IsStoreFile(found.File))
            .Select(found => $"{found.File}: {string.Join(", ", found.Sql)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("SQL lives inside a store; a caller asks the store's interface for the use case");
    }

    [Fact]
    public void No_source_file_outside_the_stores_names_sqlite_or_entity_framework()
    {
        var offenders = SourceText.CSharpUnder("src")
            .Select(file => (File: SourceText.RepoPath(file), Names: DatabaseNamespaces.Where(File.ReadAllText(file).Contains).ToList()))
            .Where(found => found.Names.Count > 0 && !IsStoreFile(found.File))
            .Select(found => $"{found.File}: {string.Join(", ", found.Names)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("the hosts and every other project reach a database only through a store's interface, which the type rule cannot see in the hosts");
    }

    [Fact]
    public void The_rules_see_the_stores_they_allow()
    {
        Types.InAssembly(typeof(Scout.Audit.IRepositoryAudits).Assembly).That().HaveDependencyOnAny(DatabaseNamespaces).GetTypes()
            .ShouldContain(type => type.FullName == "Scout.Audit.SqliteAuditStore", "the dependency scan must see the audit store's own Entity Framework use");
        SourceText.CSharpUnder("src").Select(SourceText.RepoPath).Where(IsStoreFile).Count(file => SqlIn(File.ReadAllText(Path.Combine(ArchitectureTests.SolutionRoot(), file))).Any())
            .ShouldBeGreaterThan(0, "the scan must find the SQL the knowledge store keeps, or it is reading nothing");
    }

    [Theory]
    [InlineData("var sql = \"SELECT id FROM audit_job WHERE status = $status\";", true)]
    [InlineData("command.CommandText = \"\"\"\n    INSERT INTO lines (meeting_id) VALUES ($id);\n\"\"\";", true)]
    [InlineData("const string Schema = \"CREATE TABLE IF NOT EXISTS keeps (key TEXT)\";", true)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"PRAGMA secure_delete = ON;\");", true)]
    [InlineData("var jobs = db.Jobs.Where(j => j.Status == Running).OrderBy(j => j.Started);", false)]
    [InlineData("/// <summary>Select the first pending batch and update its start.</summary>", false)]
    public void The_detector_separates_a_sql_statement_from_code_about_selecting(string line, bool sql) =>
        SqlIn(line).Any().ShouldBe(sql);

    [Theory]
    [InlineData("Scout.Audit.SqliteAuditStore", true)]
    [InlineData("Scout.Audit.SqliteAuditStore.<>c__DisplayClass5_0", true)]
    [InlineData("Scout.Audit.Storage.Migrations.Initial", true)]
    [InlineData("Scout.Audit.AuditWorker", false)]
    [InlineData("Scout.Meetings.MeetingsServiceCollectionExtensions", false)]
    public void The_allow_list_names_the_stores_only(string type, bool store) =>
        IsStoreType(type).ShouldBe(store);

    private sealed class JsonFileFakeStore;
}
