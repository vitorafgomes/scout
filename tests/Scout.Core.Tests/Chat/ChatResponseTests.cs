using Scout.Core.Chat;

namespace Scout.Core.Tests.Chat;

public class ChatResponseTests
{
    [Fact]
    public void Requires_NonEmpty_Text()
    {
        Action create = () => new ChatResponse(Text: string.Empty);

        create.ShouldThrow<ArgumentException>()
            .ParamName.ShouldBe(nameof(ChatResponse.Text));
    }

    [Fact]
    public void Requires_NonWhitespace_Text()
    {
        Action create = () => new ChatResponse(Text: "   ");

        create.ShouldThrow<ArgumentException>()
            .ParamName.ShouldBe(nameof(ChatResponse.Text));
    }

    [Fact]
    public void Citations_Default_To_Empty_Not_Null()
    {
        var response = new ChatResponse("hello");

        response.Citations.ShouldNotBeNull();
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void Plain_Produces_Text_Only_Response_With_Empty_Citations()
    {
        var response = ChatResponse.Plain("done");

        response.Text.ShouldBe("done");
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void Honors_Supplied_Citations()
    {
        string[] sources = ["file-a.md:10-20", "file-b.md:1-5"];

        var response = new ChatResponse("look here", sources);

        response.Citations.ShouldBe(sources);
    }
}
