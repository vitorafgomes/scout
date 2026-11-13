using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

public sealed class SkillSelectorTests : IDisposable
{
    private readonly string _dir;

    public SkillSelectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scout-skillsel-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private void WriteSkill(string name, string description, string body)
        => File.WriteAllText(
            Path.Combine(_dir, name + ".md"),
            $"---\nname: {name}\ndescription: {description}\n---\n{body}");

    private SkillSelector Selector(
        FakeEmbeddingGenerator embedder,
        int topN = 1,
        double threshold = 0.55,
        TimeProvider? timeProvider = null,
        ILogger<SkillSelector>? logger = null)
    {
        var options = Options.Create(new SkillsOptions
        {
            Directory = _dir,
            TopN = topN,
            SimilarityThreshold = threshold,
        });
        var loader = new SkillFileLoader(NullLogger<SkillFileLoader>.Instance);
        return new SkillSelector(
            loader, embedder, options, logger ?? NullLogger<SkillSelector>.Instance, timeProvider);
    }

    private FakeEmbeddingGenerator TwoSkillEmbedder()
    {
        WriteSkill("alpha", "alpha description", "alpha body");
        WriteSkill("beta", "beta description", "beta body");
        return new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
            ["beta description"] = [0f, 1f],
            ["query"] = [1f, 0f],
        });
    }

    [Fact]
    public async Task A_Failed_Index_Build_Is_Retried_After_The_Interval()
    {
        var embedder = TwoSkillEmbedder();
        embedder.IndexFailuresRemaining = 1;
        var clock = new FakeTimeProvider();
        var selector = Selector(embedder, threshold: 0.0, timeProvider: clock);

        var whileFailing = await selector.SelectAsync("query", default);
        clock.Advance(SkillSelector.IndexRetryInterval + TimeSpan.FromSeconds(1));
        var afterInterval = await selector.SelectAsync("query", default);

        whileFailing.ShouldBeEmpty();
        afterInterval.Select(s => s.Name).ShouldBe(["alpha"]);
        embedder.IndexBatchCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task No_Embeddings_Selects_Nothing_Puts_The_Catalog_In_The_Prompt_With_One_Information_Line_And_No_Retry()
    {
        var embedder = TwoSkillEmbedder();
        embedder.IndexFailure = new NotSupportedException("Embeddings are not available.");
        embedder.IndexFailuresRemaining = 100;
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<SkillSelector>();
        var selector = Selector(embedder, threshold: 0.0, timeProvider: clock, logger: logger);

        for (var i = 0; i < 3; i++)
        {
            (await selector.SelectAsync("query", default)).ShouldBeEmpty();
            clock.Advance(SkillSelector.IndexRetryInterval + TimeSpan.FromSeconds(1));
        }

        selector.ModelChoiceCatalog.Select(s => s.Name).ShouldBe(["alpha", "beta"]);
        embedder.IndexBatchCallCount.ShouldBe(1);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe(
            "Skill auto-selection without embeddings: the AI provider has none, so a skill that declares functions is picked by the words of the message and the skill catalog is in the prompt for the model to load the others.");
        entry.Exception.ShouldBeNull();
    }

    private const string IssuesDescription = "Opening a GitHub issue - \"open an issue\", \"abre uma issue\".";
    private const string PythonDescription = "Python typing and packaging";

    private void WriteIssuesAndPythonSkills()
    {
        File.WriteAllText(
            Path.Combine(_dir, "issues.md"),
            $"---\nname: github-issues\ndescription: {IssuesDescription}\nfunctions: GitHubActions.create_issue\n---\nOpen it.");
        WriteSkill("python", PythonDescription, "Use types.");
    }

    [Fact]
    public async Task Without_Embeddings_A_Message_Picks_The_Skill_With_Functions_Its_Words_Describe_And_The_Catalog_Stays()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>())
        {
            IndexFailure = new NotSupportedException("Embeddings are not available."),
            IndexFailuresRemaining = 100,
        };
        var selector = Selector(embedder);

        var picked = await selector.SelectAsync("abre uma issue sobre o login", default);
        var none = await selector.SelectAsync("hello there", default);

        picked.Select(s => s.Name).ShouldBe(["github-issues"]);
        none.ShouldBeEmpty();
        selector.ModelChoiceCatalog.Select(s => s.Name).ShouldBe(["github-issues", "python"]);
    }

    [Fact]
    public async Task A_Failed_Index_Build_Picks_By_The_Words_Of_The_Message()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>()) { IndexFailuresRemaining = 1 };
        var selector = Selector(embedder);

        var picked = await selector.SelectAsync("open an issue about the crash", default);

        picked.Select(s => s.Name).ShouldBe(["github-issues"]);
        selector.ModelChoiceCatalog.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Missing_Embedding_Model_Is_Logged_Once_By_Name_With_The_Fix_And_No_Stack_And_Skills_Are_Picked_By_Words()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>())
        {
            IndexFailure = new HttpRequestException(
                "Response status code does not indicate success: 404 (Not Found).", null, System.Net.HttpStatusCode.NotFound),
            IndexFailuresRemaining = 2,
            Metadata = new EmbeddingGeneratorMetadata("ollama", null, "nomic-embed-text"),
        };
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<SkillSelector>();
        var selector = Selector(embedder, timeProvider: clock, logger: logger);

        var first = await selector.SelectAsync("open an issue about the crash", default);
        clock.Advance(SkillSelector.IndexRetryInterval + TimeSpan.FromSeconds(1));
        var second = await selector.SelectAsync("abre uma issue sobre o login", default);

        embedder.IndexBatchCallCount.ShouldBe(2);
        first.Select(s => s.Name).ShouldBe(["github-issues"]);
        second.Select(s => s.Name).ShouldBe(["github-issues"]);
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Exception.ShouldBeNull();
        warning.Message.ShouldContain("nomic-embed-text", Case.Sensitive);
        warning.Message.ShouldContain("Scout Desktop", Case.Sensitive);
        warning.Message.ShouldNotContain("no skills");
    }

    [Fact]
    public async Task A_Missing_Embedding_Model_On_A_Query_Is_Logged_Once_By_Name_Without_A_Stack_And_The_Pick_Is_By_Words()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [IssuesDescription] = [1f, 0f],
            [PythonDescription] = [0f, 1f],
        })
        {
            QueryFailure = new HttpRequestException(
                "Response status code does not indicate success: 404 (Not Found).", null, System.Net.HttpStatusCode.NotFound),
            Metadata = new EmbeddingGeneratorMetadata("ollama", null, "nomic-embed-text"),
        };
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<SkillSelector>();
        var selector = Selector(embedder, timeProvider: clock, logger: logger);

        var first = await selector.SelectAsync("open an issue about the crash", default);
        clock.Advance(SkillSelector.IndexRetryInterval + TimeSpan.FromSeconds(1));
        var second = await selector.SelectAsync("abre uma issue sobre o login", default);

        embedder.IndexBatchCallCount.ShouldBe(1);
        (embedder.BatchCallCount - embedder.IndexBatchCallCount).ShouldBe(2, "each message after the retry interval asks the embedder again");
        first.Select(s => s.Name).ShouldBe(["github-issues"]);
        second.Select(s => s.Name).ShouldBe(["github-issues"]);
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Exception.ShouldBeNull();
        warning.Message.ShouldContain("nomic-embed-text", Case.Sensitive);
        warning.Message.ShouldContain("Scout Desktop", Case.Sensitive);
    }

    [Fact]
    public async Task A_Failed_Query_Embedding_Picks_By_The_Words_Of_The_Message()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [IssuesDescription] = [1f, 0f],
            [PythonDescription] = [0f, 1f],
        });
        var selector = Selector(embedder);

        var picked = await selector.SelectAsync("open an issue about the crash", default);

        picked.Select(s => s.Name).ShouldBe(["github-issues"]);
        embedder.IndexBatchCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task After_A_Failed_Query_Embedding_The_Next_Messages_Pick_By_Words_Without_Embedding_Until_The_Interval_Passes()
    {
        WriteIssuesAndPythonSkills();
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [IssuesDescription] = [1f, 0f],
            [PythonDescription] = [0f, 1f],
        });
        var clock = new FakeTimeProvider();
        var selector = Selector(embedder, timeProvider: clock);

        var first = await selector.SelectAsync("open an issue about the crash", default);
        var queriesAfterFirst = embedder.BatchCallCount - embedder.IndexBatchCallCount;
        clock.Advance(SkillSelector.IndexRetryInterval - TimeSpan.FromSeconds(1));
        var within = await selector.SelectAsync("open an issue about the login", default);
        var queriesWithin = embedder.BatchCallCount - embedder.IndexBatchCallCount;
        clock.Advance(TimeSpan.FromSeconds(2));
        var after = await selector.SelectAsync("open an issue about the logout", default);
        var queriesAfter = embedder.BatchCallCount - embedder.IndexBatchCallCount;

        (queriesAfterFirst, queriesWithin, queriesAfter).ShouldBe((1, 1, 2));
        new[] { first, within, after }.ShouldAllBe(picked => picked.Count == 1 && picked[0].Name == "github-issues");
    }

    private const string AuditDescription = "Auditing a whole repository";
    private const string ReviewDescription = "Reviewing a pull request";

    [Theory]
    [InlineData("review PR 5 looking for bugs and security problems", "code-review")]
    [InlineData("faz o review do PR 5 e veja se tem problemas de segurança", "code-review")]
    [InlineData("o PR 5 tem problemas de segurança?", "code-review")]
    [InlineData("audita o repositório acme/shop, o PR 5 mexeu em tudo", "code-audit")]
    [InlineData("procura bugs no repositório", "code-audit")]
    public async Task With_Embeddings_A_Code_Review_Of_A_Named_Pull_Request_Is_A_Review_Even_When_Its_Vector_Is_Nearest_The_Audit(string message, string skill)
    {
        File.WriteAllText(Path.Combine(_dir, "audit.md"), $"---\nname: code-audit\ndescription: {AuditDescription}\nfunctions: GitHubActions.create_issue\n---\nAudit.");
        File.WriteAllText(Path.Combine(_dir, "review.md"), $"---\nname: code-review\ndescription: {ReviewDescription}\nfunctions: GitHubActions.submit_pull_request_review\n---\nReview.");
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [AuditDescription] = [1f, 0f],
            [ReviewDescription] = [0.8f, 0.6f],
            [message] = [1f, 0f],
        });

        var picked = await Selector(embedder).SelectAsync(message, default);

        picked.Select(s => s.Name).ShouldBe([skill]);
    }

    [Fact]
    public async Task With_Embeddings_The_Prompt_Carries_The_Selected_Skill_And_No_Skills_Section()
    {
        var selector = Selector(TwoSkillEmbedder(), threshold: 0.0);

        var selected = await selector.SelectAsync("query", default);

        var prompt = SkillPromptComposer.Compose("base", selected, selector.ModelChoiceCatalog);

        selected.Select(s => s.Name).ShouldBe(["alpha"]);
        selector.ModelChoiceCatalog.ShouldBeEmpty();
        prompt.ShouldNotContain("# Skills");
        prompt.ShouldBe("base\n\n# Relevant skill: alpha\nalpha body");
    }

    [Fact]
    public async Task Repeated_Index_Failures_Log_The_Stack_Once_Then_Short_Lines()
    {
        var embedder = TwoSkillEmbedder();
        embedder.IndexFailuresRemaining = 3;
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<SkillSelector>();
        var selector = Selector(embedder, threshold: 0.0, timeProvider: clock, logger: logger);

        for (var i = 0; i < 3; i++)
        {
            await selector.SelectAsync("query", default);
            clock.Advance(SkillSelector.IndexRetryInterval + TimeSpan.FromSeconds(1));
        }

        embedder.IndexBatchCallCount.ShouldBe(3);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(3);
        warnings[0].Exception.ShouldBeOfType<HttpRequestException>();
        warnings[0].Message.ShouldContain("picking skills by the words of the message until a retry after", Case.Sensitive);
        warnings[0].Message.ShouldNotContain("no skills");
        warnings[1].Exception.ShouldBeNull();
        warnings[2].Exception.ShouldBeNull();
        warnings[1].Message.ShouldContain("429 Too Many Requests");
    }

    [Fact]
    public async Task A_Failed_Index_Build_Is_Not_Retried_Before_The_Interval()
    {
        var embedder = TwoSkillEmbedder();
        embedder.IndexFailuresRemaining = 1;
        var clock = new FakeTimeProvider();
        var selector = Selector(embedder, threshold: 0.0, timeProvider: clock);

        await selector.SelectAsync("query", default);
        clock.Advance(SkillSelector.IndexRetryInterval - TimeSpan.FromSeconds(1));
        var stillFailing = await selector.SelectAsync("query", default);

        embedder.BatchCallCount.ShouldBe(1, "a failing index must not add an embeddings call to every message");
        stillFailing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_First_Calls_Build_The_Index_Once()
    {
        var embedder = TwoSkillEmbedder();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        embedder.IndexGate = gate.Task;
        var selector = Selector(embedder, threshold: 0.0);

        var first = Task.Run(() => selector.SelectAsync("query", default));
        await embedder.IndexBatchEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var others = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => selector.SelectAsync("query", default)))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(others.Prepend(first));

        embedder.IndexBatchCallCount.ShouldBe(1);
        results.ShouldAllBe(r => r.Count == 1 && r[0].Name == "alpha");
    }

    [Fact]
    public async Task SelectAsync_Returns_Highest_Cosine_Match()
    {
        WriteSkill("alpha", "alpha description", "alpha body");
        WriteSkill("beta", "beta description", "beta body");

        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
            ["beta description"] = [0f, 1f],
            ["query"] = [0.1f, 1f],
        });

        var skills = await Selector(embedder, topN: 1).SelectAsync("query", default);

        skills.Count.ShouldBe(1);
        skills[0].Name.ShouldBe("beta");
    }

    [Fact]
    public async Task SelectAsync_Respects_TopN_And_Ranking_Order()
    {
        WriteSkill("alpha", "alpha description", "alpha body");
        WriteSkill("beta", "beta description", "beta body");
        WriteSkill("gamma", "gamma description", "gamma body");

        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
            ["beta description"] = [0.9f, 0.1f],
            ["gamma description"] = [0f, 1f],
            ["query"] = [1f, 0f],
        });

        var skills = await Selector(embedder, topN: 2, threshold: 0.0).SelectAsync("query", default);

        skills.Count.ShouldBe(2);
        skills[0].Name.ShouldBe("alpha");
        skills[1].Name.ShouldBe("beta");
    }

    [Fact]
    public async Task SelectAsync_Returns_Empty_When_Best_Below_Threshold()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
            // Orthogonal query: cosine 0, below threshold.
            ["query"] = [0f, 1f],
        });

        var skills = await Selector(embedder, threshold: 0.55).SelectAsync("query", default);

        skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_Builds_Index_Once_Across_Calls()
    {
        WriteSkill("alpha", "alpha description", "alpha body");
        WriteSkill("beta", "beta description", "beta body");

        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
            ["beta description"] = [0f, 1f],
            ["query"] = [1f, 0f],
        });

        var selector = Selector(embedder, threshold: 0.0);

        await selector.SelectAsync("query", default);
        var indexBuildsAfterFirst = embedder.IndexBatchCallCount;
        await selector.SelectAsync("query", default);

        // Descriptions embedded once; the second select reuses the cached index.
        indexBuildsAfterFirst.ShouldBe(1);
        embedder.IndexBatchCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SelectAsync_Returns_Empty_For_Blank_Message()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            ["alpha description"] = [1f, 0f],
        });

        var skills = await Selector(embedder).SelectAsync("   ", default);

        skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_Returns_Empty_When_Embedder_Throws()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        var embedder = new FakeEmbeddingGenerator(
            new Dictionary<string, float[]>(),
            throwOnGenerate: true);

        var selector = Selector(embedder);

        IReadOnlyList<Skill> skills = [];
        await Should.NotThrowAsync(async () => skills = await selector.SelectAsync("query", default));

        skills.ShouldBeEmpty();
    }
}
