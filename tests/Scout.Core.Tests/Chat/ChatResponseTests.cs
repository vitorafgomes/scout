using Scout.Core.Chat;

namespace Scout.Core.Tests.Chat;

public class ChatResponseTests
{
    [Fact]
    public void Requires_NonEmpty_Text()
    {
        Action create = () => new ChatResponse(Text: string.Empty);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ChatResponse.Text));
    }

    [Fact]
    public void Requires_NonWhitespace_Text()
    {
        Action create = () => new ChatResponse(Text: "   ");

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ChatResponse.Text));
    }

    [Fact]
    public void Citations_Default_To_Empty_Not_Null()
    {
        var response = new ChatResponse("hello");

        response.Citations.Should().NotBeNull();
        response.Citations.Should().BeEmpty();
    }

    [Fact]
    public void Plain_Produces_Text_Only_Response_With_Empty_Citations()
    {
        var response = ChatResponse.Plain("done");

        response.Text.Should().Be("done");
        response.Citations.Should().BeEmpty();
    }

    [Fact]
    public void Honors_Supplied_Citations()
    {
        string[] sources = ["file-a.md:10-20", "file-b.md:1-5"];

        var response = new ChatResponse("look here", sources);

        response.Citations.Should().Equal(sources);
    }
}
