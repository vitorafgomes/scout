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
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new SkillsOptions
        {
            Directory = _dir,
            TopN = topN,
            SimilarityThreshold = threshold,
        });
        var loader = new SkillFileLoader(NullLogger<SkillFileLoader>.Instance);
        return new SkillSelector(loader, embedder, options, NullLogger<SkillSelector>.Instance, timeProvider);
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
