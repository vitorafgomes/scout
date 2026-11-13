using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scout.Knowledge.Tests;

public sealed class ProjectIndexerTests : IDisposable
{
    private const int Dimensions = 768;

    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"scout-indexer-{Guid.NewGuid():N}");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scout-indexer-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public ProjectIndexerTests()
    {
        Directory.CreateDirectory(_repoPath);
        File.WriteAllText(
            Path.Combine(_repoPath, "Foo.cs"),
            "public class Foo {\n  public int Bar() { return 1; }\n}\n");
        File.WriteAllText(
            Path.Combine(_repoPath, "README.md"),
            "# Title\n\nSome documentation text.\n");

        var binDir = Path.Combine(_repoPath, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "skip.cs"), "public class Skipped { }\n");
    }

    private static GeneratedEmbeddings<Embedding<float>> Embeddings(int count)
    {
        var list = new GeneratedEmbeddings<Embedding<float>>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new Embedding<float>(new float[Dimensions]));
        }

        return list;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> StubGenerator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Embeddings(((IEnumerable<string>)ci[0]).Count()));
        return generator;
    }

    private static IProjectKnowledgeStore CaptureStore(out Func<IReadOnlyList<ChunkRecord>?> captured)
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        IReadOnlyList<ChunkRecord>? records = null;
        store
            .ReplaceProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => records = ci.Arg<IReadOnlyList<ChunkRecord>>());
        captured = () => records;
        return store;
    }

    [Fact]
    public async Task IndexAsync_ChunksEmbedsAndReplacesProject()
    {
        var store = CaptureStore(out var captured);
        var indexer = new ProjectIndexer(new FileChunker(), StubGenerator(), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        await store.Received(1).ReplaceProjectAsync(
            "proj",
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());

        var records = captured();
        records.ShouldNotBeNull();
        records!.Count.ShouldBeGreaterThan(0);
        records.ShouldAllBe(r => r.Project == "proj");
        records.ShouldAllBe(r => !r.Path.Contains('\\'));
        records.ShouldAllBe(r => !r.Path.Contains("bin/"));
        records.ShouldContain(r => r.Path == "Foo.cs" && r.Kind == "code");
        records.ShouldContain(r => r.Path == "README.md" && r.Kind == "doc");
        records.ShouldAllBe(r => r.Embedding.Length == Dimensions);
        records.ShouldAllBe(r => !string.IsNullOrEmpty(r.Content));
        records.ShouldAllBe(r => r.Id.StartsWith("proj:"));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> UnitGenerator()
    {
        var vector = new float[Dimensions];
        vector[0] = 1f;
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new GeneratedEmbeddings<Embedding<float>>(
                ((IEnumerable<string>)ci[0]).Select(_ => new Embedding<float>(vector))));
        return generator;
    }

    private async Task<(string[] Text, string[] Vectors)> RowsAsync(SqliteProjectKnowledgeStore store, string project)
    {
        var text = new List<string>();
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, content FROM chunk_fts WHERE project = $project ORDER BY id;";
            command.Parameters.AddWithValue("$project", project);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                text.Add($"{reader.GetString(0)}|{reader.GetString(1)}");
            }
        }

        var query = new float[Dimensions];
        query[0] = 1f;
        var vectors = await store.SearchAsync(project, query, top: 100, CancellationToken.None);
        return ([.. text], [.. vectors.Select(r => r.Id).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task IndexAsync_When_The_Embedder_Fails_Throws_And_Leaves_The_Existing_Index_Intact()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        var project = new ProjectEntry { Name = "proj", RepoPath = _repoPath };
        await new ProjectIndexer(new FileChunker(), UnitGenerator(), store, NullLogger<ProjectIndexer>.Instance)
            .IndexAsync(project, CancellationToken.None);
        var (textBefore, vectorsBefore) = await RowsAsync(store, "proj");
        textBefore.Length.ShouldBe(3);
        vectorsBefore.Length.ShouldBe(3);
        File.WriteAllText(Path.Combine(_repoPath, "Foo.cs"), "public class Renamed { }\n");
        var down = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        down
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("Connection refused")));

        var failure = await Should.ThrowAsync<HttpRequestException>(() =>
            new ProjectIndexer(new FileChunker(), down, store, NullLogger<ProjectIndexer>.Instance).IndexAsync(project, CancellationToken.None));

        failure.Message.ShouldBe("Connection refused");
        var (textAfter, vectorsAfter) = await RowsAsync(store, "proj");
        textAfter.ShouldBe(textBefore);
        vectorsAfter.ShouldBe(vectorsBefore);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task IndexAsync_Skips_A_File_It_Cannot_Read_With_One_Warning_And_Indexes_The_Rest()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var store = CaptureStore(out var captured);
        var logger = new RecordingLogger();
        File.SetUnixFileMode(Path.Combine(_repoPath, "Foo.cs"), UnixFileMode.None);
        var indexer = new ProjectIndexer(new FileChunker(), StubGenerator(), store, logger);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        captured().ShouldNotBeNull().Select(r => r.Path).ShouldBe(["README.md"]);
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldBe("Skipping file that could not be read: Foo.cs");
        warning.Exception.ShouldBeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task IndexAsync_Gives_Two_Declarations_That_Start_On_One_Line_Distinct_Ids_So_Each_Keeps_Its_Vector()
    {
        File.WriteAllText(Path.Combine(_repoPath, "OneLine.cs"), "public class Pair { int _left, _right; void Swap() { } }\n");
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        var indexer = new ProjectIndexer(new FileChunker(), UnitGenerator(), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        var (text, vectors) = await RowsAsync(store, "proj");
        string[] oneLine = ["proj:OneLine.cs:1:0", "proj:OneLine.cs:1:1", "proj:OneLine.cs:1:2", "proj:OneLine.cs:1:3"];
        vectors.Where(id => id.StartsWith("proj:OneLine.cs:", StringComparison.Ordinal)).ShouldBe(["proj:OneLine.cs:1:0", "proj:OneLine.cs:1:3"]);
        text.Where(row => row.StartsWith("proj:OneLine.cs:", StringComparison.Ordinal)).Select(row => row.Split('|')[0]).ShouldBe(oneLine);
        (await store.FindSymbolAsync("proj", "", kind: null, top: 0, CancellationToken.None))
            .Where(r => r.Path == "OneLine.cs").Select(r => r.Symbol).ShouldBe(["Pair", "_left", "_right", "Swap"]);
    }

    private async Task<SqliteProjectKnowledgeStore> StoreWithIdsOfTheOldFormatAsync()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        var vector = new float[Dimensions];
        vector[0] = 1f;
        ChunkRecord Old(string path, int line) => new()
        {
            Id = $"proj:{path}:{line}",
            Project = "proj",
            Path = path,
            Kind = "code",
            Symbol = "Old",
            StartLine = line,
            EndLine = line,
            Content = "old content",
            Embedding = vector,
        };
        await store.ReplaceProjectAsync(
            "proj", [Old("Foo.cs", 1), Old("Foo.cs", 2), Old("README.md", 1), Old("Deleted.cs", 1)], CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task IndexAsync_Over_An_Index_Whose_Ids_Have_The_Old_Format_Leaves_None_Of_Them_Behind()
    {
        var store = await StoreWithIdsOfTheOldFormatAsync();
        var indexer = new ProjectIndexer(new FileChunker(), UnitGenerator(), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        string[] expected = ["proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1:0"];
        var (text, vectors) = await RowsAsync(store, "proj");
        vectors.ShouldBe(expected);
        text.Select(row => row.Split('|')[0]).ShouldBe(expected);
    }

    [Fact]
    public async Task Reindexing_One_File_Over_Ids_Of_The_Old_Format_Leaves_None_Of_That_Files_Old_Ids_Behind()
    {
        var store = await StoreWithIdsOfTheOldFormatAsync();
        var project = new ProjectEntry { Name = "proj", RepoPath = _repoPath };
        var indexer = new ProjectIndexer(new FileChunker(), UnitGenerator(), store, NullLogger<ProjectIndexer>.Instance);

        var records = await indexer.BuildRecordsForFileAsync(project, Path.Combine(_repoPath, "Foo.cs"), CancellationToken.None);
        await store.ReplaceFileAsync("proj", "Foo.cs", records, CancellationToken.None);

        string[] expected = ["proj:Deleted.cs:1", "proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1"];
        var (text, vectors) = await RowsAsync(store, "proj");
        vectors.ShouldBe(expected);
        text.Select(row => row.Split('|')[0]).ShouldBe(expected);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> RecordingGenerator(List<string> inputs)
    {
        var vector = new float[Dimensions];
        vector[0] = 1f;
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var batch = ((IEnumerable<string>)call[0]).ToList();
                inputs.AddRange(batch);
                return new GeneratedEmbeddings<Embedding<float>>(batch.Select(_ => new Embedding<float>(vector)));
            });
        return generator;
    }

    [Fact]
    public async Task IndexAsync_Embeds_A_Chunk_Longer_Than_The_Cap_From_Its_First_16000_Characters_And_Stores_It_Whole()
    {
        var text = "The ledger opens. " + new string('x', 20_000) + " The ledger closes.";
        File.WriteAllText(Path.Combine(_repoPath, "Long.md"), text);
        var store = CaptureStore(out var captured);
        var inputs = new List<string>();
        var indexer = new ProjectIndexer(new FileChunker(), RecordingGenerator(inputs), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        var sent = inputs.Where(input => input.StartsWith("File: Long.md | doc | lines 1-1\nThe ledger opens. ", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        sent.Length.ShouldBe(16_000);
        sent.ShouldNotContain("The ledger closes.");
        inputs.ShouldAllBe(input => input.Length <= 16_000);
        captured().ShouldNotBeNull().Where(r => r.Path == "Long.md").ShouldHaveSingleItem().Content.ShouldBe(text);
    }

    [Fact]
    public async Task IndexAsync_Caps_An_Embedding_Input_Before_A_Character_That_The_Cap_Would_Cut_In_Half()
    {
        const string Header = "File: Emoji.md | doc | lines 1-1\n";
        var text = new string('a', 16_000 - Header.Length - 1) + "\U0001F600 and the rest";
        File.WriteAllText(Path.Combine(_repoPath, "Emoji.md"), text);
        var inputs = new List<string>();
        var indexer = new ProjectIndexer(new FileChunker(), RecordingGenerator(inputs), CaptureStore(out _), NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        var sent = inputs.Where(input => input.StartsWith(Header, StringComparison.Ordinal)).ShouldHaveSingleItem();
        sent.ShouldBe(Header + new string('a', 16_000 - Header.Length - 1));
    }

    [Fact]
    public async Task IndexAsync_Never_Asks_The_Embedder_For_A_Field_Stores_It_Without_A_Vector_And_Finds_It_By_Name()
    {
        File.WriteAllText(
            Path.Combine(_repoPath, "Basket.cs"),
            "public class Basket\n{\n    private int _count, _limit;\n    public int Total() => _count;\n}\n");
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        var inputs = new List<string>();
        var indexer = new ProjectIndexer(new FileChunker(), RecordingGenerator(inputs), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        inputs.Where(input => input.StartsWith("File: Basket.cs", StringComparison.Ordinal)).Select(input => input.Split('\n')[0]).ShouldBe(
            ["File: Basket.cs | code:Basket | lines 1-5", "File: Basket.cs | code:Total | lines 4-4"]);
        var (text, vectors) = await RowsAsync(store, "proj");
        vectors.Where(id => id.StartsWith("proj:Basket.cs:", StringComparison.Ordinal)).ShouldBe(["proj:Basket.cs:1:0", "proj:Basket.cs:4:3"]);
        text.Where(row => row.StartsWith("proj:Basket.cs:", StringComparison.Ordinal)).Select(row => row.Split('|')[0])
            .ShouldBe(["proj:Basket.cs:1:0", "proj:Basket.cs:3:1", "proj:Basket.cs:3:2", "proj:Basket.cs:4:3"]);
        var found = (await store.FindSymbolAsync("proj", "_limit", kind: null, top: 5, CancellationToken.None)).ShouldHaveSingleItem();
        found.Id.ShouldBe("proj:Basket.cs:3:2");
        found.Content.ShouldBe("private int _count, _limit;");
    }

    [Fact]
    public async Task A_File_Holding_Only_Fields_Makes_No_Embedding_Call_And_Its_Fields_Are_Still_Found_By_Name()
    {
        var path = Path.Combine(_repoPath, "Orphans.cs");
        File.WriteAllText(path, "namespace Shop\n{\n    internal static int _orphan;\n}\n");
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        var generator = StubGenerator();
        var indexer = new ProjectIndexer(new FileChunker(), generator, store, NullLogger<ProjectIndexer>.Instance);

        var records = await indexer.BuildRecordsForFileAsync(
            new ProjectEntry { Name = "proj", RepoPath = _repoPath }, path, CancellationToken.None);
        await store.ReplaceFileAsync("proj", "Orphans.cs", records, CancellationToken.None);

        await generator.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        records.ShouldHaveSingleItem().Embedding.Length.ShouldBe(0);
        (await store.FindSymbolAsync("proj", "_orphan", kind: null, top: 5, CancellationToken.None))
            .ShouldHaveSingleItem().Content.ShouldBe("internal static int _orphan;");
    }

    [Fact]
    public async Task IndexAsync_WithoutEmbeddings_StoresEveryChunkWithoutAVector()
    {
        var store = CaptureStore(out var captured);
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new NotSupportedException("Embeddings are not available."));
        var logger = new RecordingLogger();
        var indexer = new ProjectIndexer(new FileChunker(), generator, store, logger);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        var records = captured().ShouldNotBeNull();
        records.Select(r => r.Id).ShouldBe(["proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1:0"], ignoreOrder: true);
        records.ShouldContain(r => r.Symbol == "Bar" && r.Content.Contains("return 1;", StringComparison.Ordinal));
        records.ShouldAllBe(r => r.Embedding.Length == 0);
        await generator.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        logger.Entries.Where(e => e.Message.Contains("without embeddings", StringComparison.Ordinal))
            .ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information);
        logger.Entries.ShouldAllBe(e => e.Exception == null);
    }

    [Fact]
    public async Task IndexAsync_With_An_Embedder_Of_Another_Size_Indexes_The_Text_And_Says_Why_Vectors_Are_Off()
    {
        var store = CaptureStore(out var captured);
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new GeneratedEmbeddings<Embedding<float>>(
                ((IEnumerable<string>)ci[0]).Select(_ => new Embedding<float>(new float[1536]))));
        var logger = new RecordingLogger();
        var indexer = new ProjectIndexer(new FileChunker(), generator, store, logger);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        var records = captured().ShouldNotBeNull();
        records.Select(r => r.Id).ShouldBe(["proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1:0"], ignoreOrder: true);
        records.ShouldAllBe(r => r.Embedding.Length == 0);
        await generator.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        logger.Entries.Where(e => e.Message.StartsWith("Vectors are off", StringComparison.Ordinal)).ShouldHaveSingleItem().Message
            .ShouldBe("Vectors are off for proj: the embedding model returns 1536 dimensions, the index stores 768.");
        logger.Entries.ShouldNotContain(e => e.Message.Contains("without embeddings", StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => e.Exception == null);
    }

    [Fact]
    public async Task IndexAsync_When_The_Path_Cannot_Be_Recorded_Keeps_The_Index_It_Wrote_Warns_Once_And_Does_Not_Throw()
    {
        var store = new SqliteProjectKnowledgeStore(ConnectionString);
        await store.InitializeAsync(CancellationToken.None);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER path_insert_fails BEFORE INSERT ON project_source WHEN NEW.repo_path <> ''
                BEGIN SELECT RAISE(ABORT, 'disk full'); END;
                CREATE TRIGGER path_update_fails BEFORE UPDATE ON project_source WHEN NEW.repo_path <> ''
                BEGIN SELECT RAISE(ABORT, 'disk full'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var logger = new RecordingLogger();
        var indexer = new ProjectIndexer(new FileChunker(), UnitGenerator(), store, logger);

        await Should.NotThrowAsync(() => indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None));

        var (text, vectors) = await RowsAsync(store, "proj");
        text.Select(row => row.Split('|')[0]).ShouldBe(["proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1:0"]);
        vectors.ShouldBe(["proj:Foo.cs:1:0", "proj:Foo.cs:2:1", "proj:README.md:1:0"]);
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldBe("Indexed project proj, but could not record the path it was indexed from; it is recorded on a later use.");
        warning.Exception.ShouldBeOfType<SqliteException>();
        (await store.GetIndexedPathAsync("proj", CancellationToken.None)).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task IndexAsync_Cancelled_By_Its_Caller_While_The_Path_Is_Recorded_Throws_The_Cancellation_And_Logs_No_Warning()
    {
        using var cancellation = new CancellationTokenSource();
        var store = CaptureStore(out _);
        store.SetIndexedPathAsync("proj", _repoPath, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
        var logger = new RecordingLogger();
        var indexer = new ProjectIndexer(new FileChunker(), StubGenerator(), store, logger);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, cancellation.Token));

        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<ProjectIndexer>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            Directory.Delete(_repoPath, recursive: true);
        }

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(path);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task IndexAsync_Skips_A_Directory_It_Cannot_Read_With_One_Warning_And_Indexes_The_Rest()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var locked = Path.Combine(_repoPath, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "Hidden.cs"), "public class Hidden { }\n");
        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        File.WriteAllText(Path.Combine(_repoPath, "src", "Open.cs"), "public class Open { }\n");
        var store = CaptureStore(out var captured);
        var logger = new RecordingLogger();
        var indexer = new ProjectIndexer(new FileChunker(), StubGenerator(), store, logger);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        captured().ShouldNotBeNull().Select(r => r.Path).Distinct().Order(StringComparer.Ordinal).ShouldBe(["Foo.cs", "README.md", "src/Open.cs"]);
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldBe("Skipping directory that could not be read: locked");
        warning.Exception.ShouldBeOfType<UnauthorizedAccessException>();
    }
}
