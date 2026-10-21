using Scout.Knowledge;

namespace Scout.Knowledge.Tests;

public sealed class ProjectsOptionsTests
{
    [Fact]
    public void NewProjectsOptions_HasNoItems()
    {
        new ProjectsOptions().Items.Count.ShouldBe(0);
    }
}
