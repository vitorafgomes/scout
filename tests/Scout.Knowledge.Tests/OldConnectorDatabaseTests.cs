using System.IO.Compression;

namespace Scout.Knowledge.Tests;

/// <summary>
/// The fixture is a knowledge database written by <see cref="SqliteProjectKnowledgeStore"/> on the
/// Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview connector: project "ledger" with
/// Ledger.cs:3 on vector axis 0, Labels.cs:10 on axis 1 and README.md:1 without a vector.
/// </summary>
public sealed class OldConnectorDatabaseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scout-knowledge-old-{Guid.NewGuid():N}.db");

    public OldConnectorDatabaseTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "knowledge-1.74.0-preview.db.gz");
        using var source = new GZipStream(File.OpenRead(fixture), CompressionMode.Decompress);
        using var target = File.Create(_dbPath);
        source.CopyTo(target);
    }

    private static ReadOnlyMemory<float> Axis(int index)
    {
        var data = new float[ChunkRecord.EmbeddingDimensions];
        data[index] = 1f;
        return data;
    }

    private async Task<SqliteProjectKnowledgeStore> OpenAsync()
    {
        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task A_Database_The_Old_Connector_Wrote_Opens_And_Searches_With_The_Same_Ranking()
    {
        var store = await OpenAsync();

        (await store.HasProjectAsync("ledger", CancellationToken.None)).ShouldBeTrue();
        var nearLedger = await store.SearchAsync("ledger", Axis(0), 2, CancellationToken.None);
        var nearLabels = await store.SearchAsync("ledger", Axis(1), 2, CancellationToken.None);

        nearLedger.Select(r => r.Id).ShouldBe(["ledger:Ledger.cs:3", "ledger:Labels.cs:10"]);
        nearLabels.Select(r => r.Id).ShouldBe(["ledger:Labels.cs:10", "ledger:Ledger.cs:3"]);
        var first = nearLedger[0];
        first.Project.ShouldBe("ledger");
        first.Path.ShouldBe("Ledger.cs");
        first.Kind.ShouldBe("code");
        first.Symbol.ShouldBe("PostEntries");
        first.StartLine.ShouldBe(3);
        first.EndLine.ShouldBe(9);
        first.Content.ShouldBe("Ledger entries are posted nightly.");
        (await store.SearchTextAsync("ledger", "books", 5, CancellationToken.None))
            .Select(r => r.Id).ShouldBe(["ledger:README.md:1"]);
    }

    [Fact]
    public async Task A_Database_The_Old_Connector_Wrote_Takes_New_Rows()
    {
        var store = await OpenAsync();

        await store.ReplaceFileAsync(
            "ledger",
            "Invoices.cs",
            [new ChunkRecord
            {
                Id = "ledger:Invoices.cs:5",
                Project = "ledger",
                Path = "Invoices.cs",
                Kind = "code",
                Symbol = "IssueInvoice",
                StartLine = 5,
                EndLine = 12,
                Content = "Invoices are issued at month end.",
                Embedding = Axis(2),
            }],
            CancellationToken.None);

        (await store.SearchAsync("ledger", Axis(2), 1, CancellationToken.None))
            .Select(r => r.Id).ShouldBe(["ledger:Invoices.cs:5"]);
        (await store.SearchAsync("ledger", Axis(0), 3, CancellationToken.None))
            .Select(r => r.Id).ShouldBe(["ledger:Ledger.cs:3", "ledger:Labels.cs:10", "ledger:Invoices.cs:5"], ignoreOrder: true);
        (await store.SearchAsync("ledger", Axis(0), 3, CancellationToken.None))[0].Id.ShouldBe("ledger:Ledger.cs:3");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
