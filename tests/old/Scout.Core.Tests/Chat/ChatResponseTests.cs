using System.Runtime.CompilerServices;
using Scout.Core.Chat;

namespace Scout.Core.Tests.Chat;

public class ChatResponseTests
{
    [Fact]
    public void Plain_PreservesText()
    {
        var response = ChatResponse.Plain("done");

        response.Text.ShouldBe("done");
        response.Stream.ShouldBeNull();
        response.Citations.ShouldBeEmpty();
        response.IsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void Plain_RejectsEmptyText()
    {
        Action create = () => ChatResponse.Plain(string.Empty);

        create.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Plain_RejectsNullText()
    {
        Action create = () => ChatResponse.Plain(null!);

        create.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Streaming_HasNullText_AndStreamSet()
    {
        var stream = Empty();

        var response = ChatResponse.Streaming(stream);

        response.Text.ShouldBeNull();
        response.Stream.ShouldBeSameAs(stream);
        response.IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void Streaming_RejectsNullStream()
    {
        Action create = () => ChatResponse.Streaming(null!);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Streaming_EmptyCitations_DefaultsToEmpty()
    {
        var response = ChatResponse.Streaming(Empty());

        response.Citations.ShouldNotBeNull();
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void IsStreaming_FalseForPlain_TrueForStreaming()
    {
        ChatResponse.Plain("ok").IsStreaming.ShouldBeFalse();
        ChatResponse.Streaming(Empty()).IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void PublicCtor_DelegatesToPlain()
    {
        var citations = new[] { new Citation("src/a.cs:1") { Path = "src/a.cs", Line = 1 } };

        var viaCtor = new ChatResponse("hello", citations);
        var viaFactory = ChatResponse.Plain("hello", citations);

        viaCtor.Text.ShouldBe(viaFactory.Text);
        viaCtor.Stream.ShouldBeNull();
        viaFactory.Stream.ShouldBeNull();
        viaCtor.IsStreaming.ShouldBeFalse();
        viaFactory.IsStreaming.ShouldBeFalse();
        viaCtor.Citations.ShouldBe(viaFactory.Citations);
    }

    [Fact]
    public void PublicCtor_RejectsWhitespaceText()
    {
        Action create = () => new ChatResponse("   ");

        create.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Citations_Default_To_Empty_Not_Null()
    {
        var response = new ChatResponse("hello");

        response.Citations.ShouldNotBeNull();
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void Honors_Supplied_Citations()
    {
        var sources = new[]
        {
            new Citation("file-a.md:10") { Path = "file-a.md", Line = 10 },
            new Citation("file-b.md:1") { Path = "file-b.md", Line = 1 },
        };

        var response = new ChatResponse("look here", sources);

        response.Citations.ShouldBe(sources);
    }

    private static async IAsyncEnumerable<string> Empty(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
