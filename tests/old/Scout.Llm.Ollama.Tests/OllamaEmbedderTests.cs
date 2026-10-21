using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Embedding;

namespace Scout.Llm.Ollama.Tests;

public sealed class OllamaEmbedderTests
{
    private static CodeQAOptions ValidOptions() => new()
    {
        Embeddings = new EmbeddingOptions
        {
            Provider = "Ollama",
            Model = "nomic-embed-text",
            BaseUrl = "http://localhost:11434",
            HttpTimeoutSeconds = 30,
            MaxBatchSize = 32,
            ExpectedDimensions = 0,
        },
    };

    private static OllamaEmbedder CreateEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CodeQAOptions? options = null)
    {
        return new OllamaEmbedder(Options.Create(options ?? ValidOptions()), generator);
    }

    private static GeneratedEmbeddings<Embedding<float>> MakeResponse(params float[][] vectors)
    {
        var list = new List<Embedding<float>>(vectors.Length);
        foreach (var v in vectors)
        {
            list.Add(new Embedding<float>(v));
        }
        return new GeneratedEmbeddings<Embedding<float>>(list);
    }

    private static GeneratedEmbeddings<Embedding<float>> MakeResponse(
        IEnumerable<Embedding<float>> items) =>
        new(items);

    #region Constructor validation

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        Action act = () => new OllamaEmbedder(null!, generator);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_Embeddings_Section_Null()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = Options.Create(new CodeQAOptions { Embeddings = null! });

        Action act = () => new OllamaEmbedder(opts, generator);

        act.ShouldThrow<ArgumentException>()
           .ParamName.ShouldBe("options");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Model_Null_Or_Whitespace(string? model)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.Model = model!;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("Model");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_BaseUrl_Null_Or_Whitespace(string? url)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.BaseUrl = url!;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("BaseUrl");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:11434")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    public void Ctor_Throws_When_BaseUrl_Is_Not_Absolute_Http_Or_Https(string url)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.BaseUrl = url;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("not a valid absolute http or https URI");
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("https://ollama.internal")]
    [InlineData("http://127.0.0.1:11434/")]
    public void Ctor_Accepts_Valid_Http_And_Https_BaseUrls(string url)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.BaseUrl = url;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldNotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_HttpTimeoutSeconds_Not_Positive(int timeout)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.HttpTimeoutSeconds = timeout;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("HttpTimeoutSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_Throws_When_MaxBatchSize_Not_Positive(int batch)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.MaxBatchSize = batch;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("MaxBatchSize");
    }

    [Fact]
    public void Ctor_Throws_When_ExpectedDimensions_Negative()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var opts = ValidOptions();
        opts.Embeddings.ExpectedDimensions = -1;

        Action act = () => CreateEmbedder(generator, opts);

        act.ShouldThrow<ArgumentException>()
           .Message.ShouldContain("ExpectedDimensions");
    }

    [Fact]
    public void Public_Ctor_With_Valid_Options_Constructs_Successfully()
    {
        Action act = () => new OllamaEmbedder(Options.Create(ValidOptions()));

        act.ShouldNotThrow();
    }

    #endregion

    #region Input validation

    [Fact]
    public async Task EmbedAsync_Throws_When_Texts_Null()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<ArgumentNullException>(
            () => embedder.EmbedAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task EmbedAsync_Throws_When_Entry_Whitespace(string bad)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedder = CreateEmbedder(generator);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => embedder.EmbedAsync(["ok", bad, "also-ok"], CancellationToken.None));
        ex.ParamName.ShouldBe("texts");
    }

    [Fact]
    public async Task EmbedAsync_Throws_When_Entry_Null()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedder = CreateEmbedder(generator);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => embedder.EmbedAsync(["ok", null!, "also-ok"], CancellationToken.None));
        ex.ParamName.ShouldBe("texts");
    }

    [Fact]
    public async Task EmbedAsync_Empty_Input_Returns_Empty_Without_Network_Call()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedder = CreateEmbedder(generator);

        var result = await embedder.EmbedAsync(Array.Empty<string>(), CancellationToken.None);

        result.ShouldBeEmpty();
        await generator.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Happy path and batching

    [Fact]
    public async Task EmbedAsync_Single_Batch_Returns_Vectors_In_Order()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(
                new float[] { 1f, 2f, 3f },
                new float[] { 4f, 5f, 6f },
                new float[] { 7f, 8f, 9f }));

        var embedder = CreateEmbedder(generator);

        var result = await embedder.EmbedAsync(["a", "b", "c"], CancellationToken.None);

        result.Count.ShouldBe(3);
        result[0].ToArray().ShouldBe(new[] { 1f, 2f, 3f });
        result[1].ToArray().ShouldBe(new[] { 4f, 5f, 6f });
        result[2].ToArray().ShouldBe(new[] { 7f, 8f, 9f });
    }

    [Fact]
    public async Task EmbedAsync_Splits_Input_Into_Batches_Preserving_Order()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        // MaxBatchSize = 2, input of 5 -> batches of 2, 2, 1.
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var batch = ((IEnumerable<string>)call[0]).ToList();
                var outputs = new List<Embedding<float>>(batch.Count);
                foreach (var text in batch)
                {
                    outputs.Add(new Embedding<float>(new float[] { text[0], text.Length }));
                }
                return MakeResponse(outputs);
            });

        var opts = ValidOptions();
        opts.Embeddings.MaxBatchSize = 2;
        var embedder = CreateEmbedder(generator, opts);

        string[] inputs = ["a1", "bb", "ccc", "dddd", "eeeee"];
        var result = await embedder.EmbedAsync(inputs, CancellationToken.None);

        result.Count.ShouldBe(5);
        result[0].ToArray().ShouldBe(new float[] { 'a', 2 });
        result[1].ToArray().ShouldBe(new float[] { 'b', 2 });
        result[2].ToArray().ShouldBe(new float[] { 'c', 3 });
        result[3].ToArray().ShouldBe(new float[] { 'd', 4 });
        result[4].ToArray().ShouldBe(new float[] { 'e', 5 });

        await generator.Received(3).GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedAsync_Forwards_CancellationToken_To_Generator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(new float[] { 1f }));

        var embedder = CreateEmbedder(generator);
        using var cts = new CancellationTokenSource();

        await embedder.EmbedAsync(["hello"], cts.Token);

        await generator.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            cts.Token);
    }

    #endregion

    #region Boundary failure modes

    [Fact]
    public async Task EmbedAsync_Throws_When_Generator_Returns_Fewer_Vectors_Than_Inputs()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(new float[] { 1f, 2f }));

        var embedder = CreateEmbedder(generator);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["a", "b"], CancellationToken.None));
        ex.Message.ShouldContain("1");
        ex.Message.ShouldContain("2");
    }

    [Fact]
    public async Task EmbedAsync_Throws_When_Generator_Returns_More_Vectors_Than_Inputs()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(
                new float[] { 1f },
                new float[] { 2f },
                new float[] { 3f }));

        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["only-one"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Throws_When_Generator_Returns_Empty_Vector()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(Array.Empty<float>()));

        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Throws_When_Vector_Dimensions_Differ_From_Expected()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(new float[] { 1f, 2f, 3f }));

        var opts = ValidOptions();
        opts.Embeddings.ExpectedDimensions = 768;
        var embedder = CreateEmbedder(generator, opts);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["a"], CancellationToken.None));
        ex.Message.ShouldContain("768");
        ex.Message.ShouldContain("3");
    }

    [Fact]
    public async Task EmbedAsync_Does_Not_Check_Dimensions_When_Expected_Is_Zero()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeResponse(new float[] { 1f, 2f, 3f }));

        var embedder = CreateEmbedder(generator);

        var result = await embedder.EmbedAsync(["a"], CancellationToken.None);

        result[0].Length.ShouldBe(3);
    }

    [Fact]
    public async Task EmbedAsync_Propagates_TaskCanceledException_From_Generator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(
                _ => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new TaskCanceledException("timeout")));

        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<TaskCanceledException>(
            () => embedder.EmbedAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Propagates_HttpRequestException_From_Generator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(
                _ => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new HttpRequestException("ollama returned 500")));

        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<HttpRequestException>(
            () => embedder.EmbedAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Propagates_Generic_Exception_From_Generator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(
                _ => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new InvalidOperationException("connection reset")));

        var embedder = CreateEmbedder(generator);

        await Should.ThrowAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Stops_At_First_Failing_Batch()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var call = 0;
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ =>
            {
                call++;
                if (call == 1)
                {
                    return Task.FromResult(MakeResponse(
                        new float[] { 1f },
                        new float[] { 2f }));
                }
                return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new HttpRequestException("boom"));
            });

        var opts = ValidOptions();
        opts.Embeddings.MaxBatchSize = 2;
        var embedder = CreateEmbedder(generator, opts);

        await Should.ThrowAsync<HttpRequestException>(
            () => embedder.EmbedAsync(["a", "b", "c", "d"], CancellationToken.None));

        await generator.Received(2).GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion
}
