using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Indexing;

public class RawFileTests
{
    [Fact]
    public void Ctor_Happy_Path()
    {
        var file = new RawFile("src/Foo.cs", "abc123", 19, "public class Foo {}");

        file.Path.ShouldBe("src/Foo.cs");
        file.Sha.ShouldBe("abc123");
        file.SizeBytes.ShouldBe(19);
        file.Content.ShouldBe("public class Foo {}");
    }

    [Fact]
    public void Ctor_Allows_Empty_Content()
    {
        var file = new RawFile("src/Empty.cs", "sha", 0, string.Empty);

        file.Content.ShouldBe(string.Empty);
        file.SizeBytes.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Ctor_Rejects_Empty_Path(string path)
    {
        Action act = () => new RawFile(path, "sha", 1, "x");
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe("Path");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_Rejects_Empty_Sha(string sha)
    {
        Action act = () => new RawFile("p", sha, 1, "x");
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe("Sha");
    }

    [Fact]
    public void Ctor_Rejects_Negative_Size()
    {
        Action act = () => new RawFile("p", "sha", -1, "x");
        act.ShouldThrow<ArgumentOutOfRangeException>().ParamName.ShouldBe("SizeBytes");
    }

    [Fact]
    public void Ctor_Rejects_Null_Content()
    {
        Action act = () => new RawFile("p", "sha", 0, null!);
        act.ShouldThrow<ArgumentNullException>().ParamName.ShouldBe("Content");
    }

    [Fact]
    public void Record_Value_Equality()
    {
        var a = new RawFile("p", "sha", 1, "x");
        var b = new RawFile("p", "sha", 1, "x");

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
