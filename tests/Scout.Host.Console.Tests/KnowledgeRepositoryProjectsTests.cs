using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Knowledge;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class KnowledgeRepositoryProjectsTests
{
    [Fact]
    public void A_Snapshot_The_GitHub_Plugin_Took_Becomes_A_Project_Of_The_Knowledge_Index()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        var resolution = new ProjectResolutionService(
            Options.Create(new ProjectsOptions()),
            new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance),
            new ProjectIndexer(Substitute.For<IChunker>(), Substitute.For<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(), store, NullLogger<ProjectIndexer>.Instance),
            store,
            NullLogger<ProjectResolutionService>.Instance);

        new KnowledgeRepositoryProjects(resolution).Register("octocat/hello", "/tmp/snapshots/octocat__hello/abc");

        resolution.GetProjectNames().ShouldBe(["octocat/hello"]);
        resolution.Find("octocat/hello").ShouldNotBeNull().RepoPath.ShouldBe("/tmp/snapshots/octocat__hello/abc");
    }
}
