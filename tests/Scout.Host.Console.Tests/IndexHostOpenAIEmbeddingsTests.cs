using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// The index host on the OpenAI provider with its default embedding model, over a stub OpenAI endpoint that answers
/// the vector size the request asks for (1536 when it asks for none, like text-embedding-3-small), into a real SQLite
/// index on disk.
/// </summary>
public sealed class IndexHostOpenAIEmbeddingsTests : IDisposable
{
    private const string ApiKey = "sk-test-key-must-never-surface";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scout-openai-index-{Guid.NewGuid():N}");

    public IndexHostOpenAIEmbeddingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        File.WriteAllText(Path.Combine(_root, "repo", "README.md"), "Ledger entries are posted nightly by the billing job.");
        File.WriteAllText(Path.Combine(_root, "repo", "Ledger.cs"), "public class Ledger\n{\n    public void Post() { }\n}\n");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class EmbeddingsEndpoint(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        public bool NeverAnswers { get; set; }

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            if (NeverAnswers)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent("""{"error":{"message":"The service is unavailable.","type":"server_error"}}""", Encoding.UTF8, "application/json"),
                };
            }

            using var json = JsonDocument.Parse(body);
            var size = json.RootElement.TryGetProperty("dimensions", out var dimensions) ? dimensions.GetInt32() : 1536;
            var vector = Convert.ToBase64String(MemoryMarshal.AsBytes(Enumerable.Repeat(0.01f, size).ToArray().AsSpan()));
            var data = json.RootElement.GetProperty("input").EnumerateArray()
                .Select((_, i) => new { @object = "embedding", index = i, embedding = vector });
            var reply = JsonSerializer.Serialize(new
            {
                @object = "list",
                data,
                model = "text-embedding-3-small",
                usage = new { prompt_tokens = 1, total_tokens = 1 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel, string, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add((logLevel, formatter(state, exception), exception));
                }
            }
        }
    }

    private IHost Host(
        EmbeddingsEndpoint endpoint,
        RecordingLoggerProvider logs,
        TimeSpan? requestTimeout = null,
        string provider = "OpenAI",
        string? database = null)
    {
        var http = new HttpClient(endpoint) { Timeout = requestTimeout ?? TimeSpan.FromSeconds(100) };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = provider,
                ["Scout:Llm:OpenAI:ApiKey"] = ApiKey,
                ["Scout:Llm:OpenAI:Chat:Model"] = "gpt-4o-mini",
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                ["Scout:Projects:Items:0:Name"] = "ledger",
                ["Scout:Projects:Items:0:RepoPath"] = Path.Combine(_root, "repo"),
            })
            .Build();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddProvider(logs));
                services.AddScoutIndexHost(configuration);
                services.RemoveAll<ILlmProviderFactory>();
                services.AddSingleton<ILlmProviderFactory>(sp => provider == "Ollama"
                    ? new OllamaLlmProviderFactory(sp.GetRequiredService<IOptions<LlmOptions>>(), http)
                    : new OpenAILlmProviderFactory(sp.GetRequiredService<IOptions<LlmOptions>>(), http));
                services.RemoveAll<IProjectKnowledgeStore>();
                services.AddSingleton<IProjectKnowledgeStore>(
                    new SqliteProjectKnowledgeStore($"Data Source={database ?? Path.Combine(_root, "knowledge.db")}"));
            })
            .Build();
    }

    private ProjectEntry Project() => new() { Name = "ledger", RepoPath = Path.Combine(_root, "repo") };

    [Fact]
    public async Task Host_With_OpenAI_Embeddings_Indexes_A_Project()
    {
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        using var host = Host(endpoint, new RecordingLoggerProvider());
        var store = host.Services.GetRequiredService<IProjectKnowledgeStore>();

        await host.Services.GetRequiredService<ProjectIndexer>().IndexAsync(Project(), CancellationToken.None);

        (await store.HasProjectAsync("ledger", CancellationToken.None)).ShouldBeTrue();
        endpoint.Requests.ShouldNotBeEmpty();
        endpoint.Requests.ShouldAllBe(body => body.Contains("\"dimensions\":768"));
        var query = new float[ChunkRecord.EmbeddingDimensions];
        Array.Fill(query, 0.01f);
        (await store.SearchAsync("ledger", query, 5, CancellationToken.None)).Select(r => r.Path).Distinct()
            .ShouldBe(["Ledger.cs", "README.md"], ignoreOrder: true);
    }

    [Fact]
    public async Task Host_With_OpenAI_Embeddings_Sends_No_Input_Longer_Than_16000_Characters_And_Stores_The_Chunk_Whole()
    {
        var text = "The audit opens. " + string.Concat(Enumerable.Repeat("entry ", 8_000)) + "The audit closes with zebratail.";
        File.WriteAllText(Path.Combine(_root, "repo", "Long.md"), text);
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        using var host = Host(endpoint, new RecordingLoggerProvider());
        var store = host.Services.GetRequiredService<IProjectKnowledgeStore>();

        await host.Services.GetRequiredService<ProjectIndexer>().IndexAsync(Project(), CancellationToken.None);

        var sent = endpoint.Requests
            .SelectMany(body =>
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("input").EnumerateArray().Select(input => input.GetString()!).ToList();
            })
            .ToList();
        sent.Max(input => input.Length).ShouldBe(16_000);
        sent.Where(input => input.Length == 16_000).ShouldHaveSingleItem().ShouldStartWith("File: Long.md | doc | lines 1-1\nThe audit opens. ");
        (await store.SearchTextAsync("ledger", "zebratail", 5, CancellationToken.None)).ShouldHaveSingleItem().Content.ShouldBe(text);
    }

    [Fact]
    public async Task The_Index_Command_With_OpenAI_Embeddings_That_Fail_Logs_The_Failure_Exits_1_Keeps_The_Index_It_Had_And_Never_Logs_The_Key()
    {
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        var logs = new RecordingLoggerProvider();
        using var host = Host(endpoint, logs);
        var store = host.Services.GetRequiredService<IProjectKnowledgeStore>();
        (await IndexEntryPoint.RunAsync(host.Services, "ledger", CancellationToken.None)).ShouldBe(0);
        File.WriteAllText(Path.Combine(_root, "repo", "README.md"), "Refunds are settled weekly by the treasury job.");
        endpoint.Status = HttpStatusCode.ServiceUnavailable;

        var exitCode = await IndexEntryPoint.RunAsync(host.Services, "ledger", CancellationToken.None);

        exitCode.ShouldBe(1);
        var error = logs.Entries.Where(e => e.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldBe("Indexing failed for project 'ledger'.");
        error.Exception.ShouldNotBeNull().ToString().ShouldContain("503");
        (await store.SearchTextAsync("ledger", "nightly billing", 5, CancellationToken.None))
            .ShouldHaveSingleItem().Content.ShouldBe("Ledger entries are posted nightly by the billing job.");
        (await store.SearchTextAsync("ledger", "refunds treasury", 5, CancellationToken.None)).ShouldBeEmpty();
        var query = new float[ChunkRecord.EmbeddingDimensions];
        Array.Fill(query, 0.01f);
        (await store.SearchAsync("ledger", query, 5, CancellationToken.None)).Select(r => r.Path).Distinct()
            .ShouldBe(["Ledger.cs", "README.md"], ignoreOrder: true);
        logs.Entries.ShouldAllBe(e => !e.Message.Contains(ApiKey) && (e.Exception == null || !e.Exception.ToString().Contains(ApiKey)));
    }

    [Theory]
    [InlineData("Ollama", typeof(TaskCanceledException))]
    [InlineData("OpenAI", typeof(AggregateException))]
    public async Task The_Index_Command_Whose_Embedding_Request_Times_Out_While_Nobody_Cancelled_Logs_The_Failure_And_Exits_1(
        string provider,
        Type surfaced)
    {
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK) { NeverAnswers = true };
        var logs = new RecordingLoggerProvider();
        using var host = Host(endpoint, logs, requestTimeout: TimeSpan.FromMilliseconds(200), provider);

        var exitCode = await IndexEntryPoint.RunAsync(host.Services, "ledger", CancellationToken.None);

        exitCode.ShouldBe(1);
        var error = logs.Entries.Where(e => e.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldBe("Indexing failed for project 'ledger'.");
        error.Exception.ShouldBeOfType(surfaced);
        (await host.Services.GetRequiredService<IProjectKnowledgeStore>().HasProjectAsync("ledger", CancellationToken.None)).ShouldBeFalse();
    }
    [Fact]
    public async Task The_Index_Command_Cancelled_By_Its_Caller_Throws_The_Cancellation_And_Logs_No_Failure()
    {
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        var logs = new RecordingLoggerProvider();
        using var host = Host(endpoint, logs);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => IndexEntryPoint.RunAsync(host.Services, "ledger", cancelled.Token));

        logs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        endpoint.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Index_Command_Over_A_Database_That_Cannot_Be_Opened_Logs_The_Failure_And_Exits_1()
    {
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        var logs = new RecordingLoggerProvider();
        using var host = Host(endpoint, logs, database: Path.Combine(_root, "repo"));

        var exitCode = await IndexEntryPoint.RunAsync(host.Services, "ledger", CancellationToken.None);

        exitCode.ShouldBe(1);
        var error = logs.Entries.Where(e => e.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldBe("Indexing failed for project 'ledger'.");
        error.Exception.ShouldBeOfType<SqliteException>();
        endpoint.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Index_Command_Stopped_Through_The_Host_Lifetime_Stops_At_Its_Next_Await_Exits_1_And_Keeps_The_Index_It_Had()
    {
        File.WriteAllText(Path.Combine(_root, "repo", "Zeta.md"), "Zeta notes are filed last.");
        var endpoint = new EmbeddingsEndpoint(HttpStatusCode.OK);
        var logs = new RecordingLoggerProvider();
        using var host = Host(endpoint, logs);
        (await IndexEntryPoint.RunAsync(host.Services, "ledger", CancellationToken.None)).ShouldBe(0);
        File.WriteAllText(Path.Combine(_root, "repo", "README.md"), "Refunds are settled weekly by the treasury job.");
        var before = endpoint.Requests.Count;
        endpoint.NeverAnswers = true;

        var run = IndexEntryPoint.RunAsync(host, "ledger");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (endpoint.Requests.Count == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.ShouldBe(run, "the index command stops when the host is asked to stop");
        (await run).ShouldBe(1);
        (endpoint.Requests.Count - before).ShouldBe(1);
        logs.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message).ShouldBe(["Indexing of project 'ledger' was stopped before it finished."]);
        var store = host.Services.GetRequiredService<IProjectKnowledgeStore>();
        (await store.SearchTextAsync("ledger", "nightly billing", 5, CancellationToken.None))
            .ShouldHaveSingleItem().Content.ShouldBe("Ledger entries are posted nightly by the billing job.");
        (await store.SearchTextAsync("ledger", "refunds treasury", 5, CancellationToken.None)).ShouldBeEmpty();
    }
}
