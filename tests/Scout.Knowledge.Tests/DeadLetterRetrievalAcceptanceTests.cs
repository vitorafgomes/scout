using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Knowledge.Tests;

/// <summary>
/// Opt-in end-to-end acceptance test for the project knowledge RAG pipeline.
/// It indexes a small on-disk fixture with real Service Bus dead-letter code
/// using a real Ollama embedding model, then asserts retrieval surfaces the
/// dead-letter content for a natural-language question.
///
/// This test is gated and SKIPS by default so normal CI and a plain
/// "dotnet test" without a running Ollama daemon stay green. It runs only when
/// the environment variable <c>SCOUT_OLLAMA_ACCEPTANCE</c> is set to "1", and
/// it also no-ops (skips) if Ollama is unreachable, so it never breaks the
/// build. xunit 2.x has no Assert.Skip, so the gate is a plain early return
/// with a logged note; the assertions only execute on the enabled path.
/// </summary>
public sealed class DeadLetterRetrievalAcceptanceTests : IDisposable
{
    private const string EnableEnvVar = "SCOUT_OLLAMA_ACCEPTANCE";
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";

    private readonly string _repoPath =
        Path.Combine(Path.GetTempPath(), $"scout-deadletter-{Guid.NewGuid():N}");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"scout-deadletter-{Guid.NewGuid():N}.db");

    public DeadLetterRetrievalAcceptanceTests()
    {
        Directory.CreateDirectory(_repoPath);
        WriteFixture();
    }

    [Fact]
    public async Task SearchAsync_finds_dead_letter_content_end_to_end()
    {
        if (!IsEnabled())
        {
            // Opt-in gate: not enabled, treat as a skipped acceptance test.
            return;
        }

        IEmbeddingGenerator<string, Embedding<float>> embedder;
        try
        {
            embedder = BuildOllamaEmbedder();

            // Probe the embedding endpoint so an unreachable Ollama daemon
            // skips (returns) rather than failing the suite.
            _ = await embedder.GenerateVectorAsync("ping", cancellationToken: CancellationToken.None);
        }
        catch (Exception)
        {
            // Ollama unreachable or model unavailable: skip, never fail CI.
            return;
        }

        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        await store.InitializeAsync(CancellationToken.None);

        var indexer = new ProjectIndexer(
            new FileChunker(),
            embedder,
            store,
            NullLogger<ProjectIndexer>.Instance);

        const string project = "deadletter-fixture";

        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions
            {
                Items = { new ProjectEntry { Name = project, RepoPath = _repoPath } },
            }),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            indexer,
            store,
            NullLogger<ProjectResolutionService>.Instance);

        var service = new ProjectKnowledgeService(
            embedder,
            store,
            resolution,
            NullLogger<ProjectKnowledgeService>.Instance);

        await indexer.IndexAsync(
            new ProjectEntry { Name = project, RepoPath = _repoPath },
            CancellationToken.None);

        var context = await service.SearchAsync(
            project,
            "how are messages sent to the dead-letter queue",
            CancellationToken.None);

        context.ShouldNotBeNullOrWhiteSpace();
        context.ShouldContain("DeadLetter", Case.Insensitive);
    }

    private static bool IsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnableEnvVar),
            "1",
            StringComparison.Ordinal);

    private static IEmbeddingGenerator<string, Embedding<float>> BuildOllamaEmbedder()
    {
        var options = Options.Create(new LlmOptions
        {
            Provider = "Ollama",
            Ollama = new OllamaOptions
            {
                Endpoint = OllamaEndpoint,
                Chat = new ChatModelOptions { Model = "qwen2.5" },
                Embeddings = new EmbeddingModelOptions { Model = EmbeddingModel },
            },
        });

        return new OllamaLlmProviderFactory(options).CreateEmbeddingGenerator();
    }

    private void WriteFixture()
    {
        File.WriteAllText(
            Path.Combine(_repoPath, "DeadLetterHandler.cs"),
            """
            using System.Threading;
            using System.Threading.Tasks;

            namespace Sample.Messaging;

            // Routes poison messages to the Service Bus dead-letter queue.
            public sealed class DeadLetterHandler
            {
                // Sends a message to the dead-letter queue with a reason and description.
                public Task DeadLetterMessageAsync(
                    MessageInfo message,
                    string deadLetterReason,
                    CancellationToken cancellationToken)
                {
                    // The Service Bus receiver moves the message to the
                    // dead-letter sub-queue when processing cannot continue.
                    return Task.CompletedTask;
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(_repoPath, "MessageInfo.cs"),
            """
            namespace Sample.Messaging;

            // Carries the delivery metadata a dead-letter decision depends on.
            public sealed class MessageInfo
            {
                // Number of delivery attempts; drives the dead-letter threshold.
                public int DeliveryCount { get; init; }

                // Reason recorded when a message is dead-lettered.
                public string DeadLetterReason { get; init; } = string.Empty;
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            Directory.Delete(_repoPath, recursive: true);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
