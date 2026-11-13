using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _dir;

    public SkillCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scout-skillcat-" + Guid.NewGuid().ToString("N"));
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

    private SkillCatalog Catalog(string? directory = null)
    {
        var options = Options.Create(new SkillsOptions { Directory = directory ?? _dir });
        var loader = new SkillFileLoader(NullLogger<SkillFileLoader>.Instance);
        return new SkillCatalog(loader, options);
    }

    [Fact]
    public void Does_Not_Load_Files_Until_First_Access()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        var catalog = Catalog();

        // Construction must not read files. Deleting the directory after construction
        // but before first access still yields an empty catalog, proving the load was
        // deferred to first use rather than done in the constructor.
        System.IO.Directory.Delete(_dir, recursive: true);

        catalog.All().ShouldBeEmpty();
    }

    [Fact]
    public void All_Returns_Loaded_Skills()
    {
        WriteSkill("alpha", "alpha description", "alpha body");
        WriteSkill("beta", "beta description", "beta body");

        var skills = Catalog().All();

        skills.Count.ShouldBe(2);
        skills.Select(s => s.Name).ShouldContain("alpha");
        skills.Select(s => s.Name).ShouldContain("beta");
    }

    [Fact]
    public void Find_Returns_Skill_By_Exact_Name()
    {
        WriteSkill("debugging", "use when debugging", "debug body");

        var skill = Catalog().Find("debugging");

        skill.ShouldNotBeNull();
        skill!.Name.ShouldBe("debugging");
        skill.Body.ShouldBe("debug body");
    }

    [Fact]
    public void Find_Is_Case_Insensitive()
    {
        WriteSkill("debugging", "use when debugging", "debug body");

        Catalog().Find("DEBUGGING").ShouldNotBeNull();
    }

    [Fact]
    public void Find_Returns_Null_For_Unknown_Name()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        Catalog().Find("missing").ShouldBeNull();
    }

    [Fact]
    public void Find_Returns_Null_For_Blank_Name()
    {
        WriteSkill("alpha", "alpha description", "alpha body");

        Catalog().Find("   ").ShouldBeNull();
    }

    [Fact]
    public void All_Returns_Empty_For_Empty_Directory()
    {
        Catalog().All().ShouldBeEmpty();
    }

    [Fact]
    public void Never_Throws_For_Absent_Directory()
    {
        var catalog = Catalog(Path.Combine(_dir, "does-not-exist"));

        IReadOnlyList<Skill> skills = [];
        Should.NotThrow(() => skills = catalog.All());
        skills.ShouldBeEmpty();
        Should.NotThrow(() => catalog.Find("anything"));
    }
}
