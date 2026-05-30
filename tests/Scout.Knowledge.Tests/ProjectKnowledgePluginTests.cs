using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Knowledge;

namespace Scout.Knowledge.Tests;

/// <summary>
/// Verifies the project knowledge plugin exposes Semantic Kernel function metadata
/// (stable name, descriptions on the function and its parameters) and that calling
/// the function delegates to the wrapped service.
/// </summary>
public sealed class ProjectKnowledgePluginTests
{
    private static ProjectKnowledgeService Service(IProjectKnowledgeStore store)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
                new[] { new Embedding<float>(new float[] { 1f, 0f, 0f }) }));
        return new ProjectKnowledgeService(embedder, store, NullLogger<ProjectKnowledgeService>.Instance);
    }

    private static KernelPlugin BuildPlugin(ProjectKnowledgeService service)
        => KernelPluginFactory.CreateFromObject(new ProjectKnowledgePlugin(service), "ProjectKnowledge");

    [Fact]
    public void Exposes_Search_Project_Function()
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));

        plugin.TryGetFunction("search_project", out _).ShouldBeTrue();
    }

    [Fact]
    public void Function_Has_NonEmpty_Description()
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));
        plugin.TryGetFunction("search_project", out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("project")]
    [InlineData("question")]
    public void Function_Exposes_Described_Parameter(string parameterName)
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));
        plugin.TryGetFunction("search_project", out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Search_Returns_Cited_Context_From_Service()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.SearchAsync("scout", Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>
            {
                new()
                {
                    Id = "scout:Foo.cs:1", Project = "scout", Path = "Foo.cs", Kind = "code",
                    Symbol = "Bar", StartLine = 1, EndLine = 3, Content = "public int Bar() => 1;",
                },
            });

        var plugin = new ProjectKnowledgePlugin(Service(store));

        var result = await plugin.SearchAsync("scout", "what does Bar do?", CancellationToken.None);

        result.ShouldContain("Foo.cs:1-3");
        result.ShouldContain("public int Bar() => 1;");
    }

    [Fact]
    public async Task Search_Unknown_Project_Returns_Clean_Message()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.SearchAsync("ghost", Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>());

        var plugin = new ProjectKnowledgePlugin(Service(store));

        var result = await plugin.SearchAsync("ghost", "anything?", CancellationToken.None);

        result.ShouldContain("ghost");
    }

    [Fact]
    public void Exposes_Find_Symbol_Function()
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));

        plugin.TryGetFunction("find_symbol", out _).ShouldBeTrue();
    }

    [Fact]
    public void Find_Symbol_Function_Has_NonEmpty_Description()
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));
        plugin.TryGetFunction("find_symbol", out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("project")]
    [InlineData("symbol")]
    public void Find_Symbol_Exposes_Described_Parameter(string parameterName)
    {
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));
        plugin.TryGetFunction("find_symbol", out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("search_project")]
    [InlineData("find_symbol")]
    public void Project_Parameter_Description_Does_Not_Name_A_Concrete_Project(string functionName)
    {
        // Regression lock: the project parameter [Description] is serialized into the
        // tool schema the model sees. A concrete project name inlined there gets parroted
        // regardless of configuration, so the description must delegate to the runtime
        // agent instructions and carry no inline example.
        var plugin = BuildPlugin(Service(Substitute.For<IProjectKnowledgeStore>()));
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.Single(p => p.Name == "project");

        parameter.Description.ShouldContain("instructions");
        parameter.Description.ShouldNotContain("e.g.");
    }

    [Fact]
    public async Task Find_Symbol_Returns_Cited_Context_From_Service()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.FindSymbolAsync("scout", "Bar", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkRecord>
            {
                new()
                {
                    Id = "scout:Foo.cs:1", Project = "scout", Path = "Foo.cs", Kind = "code",
                    Symbol = "Bar", StartLine = 1, EndLine = 3, Content = "public int Bar() => 1;",
                },
            });

        var plugin = new ProjectKnowledgePlugin(Service(store));

        var result = await plugin.FindSymbolAsync("scout", "Bar", kind: null, top: 0, CancellationToken.None);

        result.ShouldContain("Foo.cs:1-3");
        result.ShouldContain("public int Bar() => 1;");
    }
}
