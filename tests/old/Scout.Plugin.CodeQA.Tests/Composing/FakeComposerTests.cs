using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Composing;

public class FakeComposerTests
{
    private static VectorSearchResult MakeHit(double score = 0.5)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f });
        var chunk = new CodeChunk("id", "x.cs", 1, 2, "x", embedding);
        return new VectorSearchResult(chunk, score);
    }

    [Fact]
    public async Task Echoes_Question_And_Maps_Citations()
    {
        var composer = new FakeComposer();

        var answer = await composer.ComposeAsync(
            "what?",
            new[] { MakeHit(0.4), MakeHit(0.2) },
            CancellationToken.None);

        answer.Text.ShouldBe("ECHO: what?");
        answer.Citations.Count.ShouldBe(2);
        answer.ModelUsed.ShouldBe("fake-model");
        answer.Latency.ShouldBe(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Throws_On_Empty_Question()
    {
        var composer = new FakeComposer();

        await Should.ThrowAsync<ArgumentException>(() =>
            composer.ComposeAsync(" ", Array.Empty<VectorSearchResult>(), CancellationToken.None));
    }

    [Fact]
    public async Task Throws_On_Null_Context()
    {
        var composer = new FakeComposer();

        await Should.ThrowAsync<ArgumentNullException>(() =>
            composer.ComposeAsync("q", null!, CancellationToken.None));
    }

    [Fact]
    public async Task Honours_Cancellation()
    {
        var composer = new FakeComposer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            composer.ComposeAsync("q", Array.Empty<VectorSearchResult>(), cts.Token));
    }

    [Fact]
    public async Task Clamps_Out_Of_Range_Score()
    {
        var composer = new FakeComposer();

        var answer = await composer.ComposeAsync(
            "q",
            new[] { MakeHit(2.0) },
            CancellationToken.None);

        answer.Citations[0].RelevanceScore.ShouldBe(1.0);
    }

    [Fact]
    public void Ctor_Throws_On_Empty_Model()
    {
        Should.Throw<ArgumentException>(() => new FakeComposer(modelUsed: " "));
    }
}
