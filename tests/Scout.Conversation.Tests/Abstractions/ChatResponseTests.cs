using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Tests.Abstractions;

public sealed class ChatResponseTests
{
    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    [Fact]
    public void Ctor_Throws_When_Tokens_Null()
    {
        Should.Throw<ArgumentNullException>(() => new ChatResponse(null!));
    }

    [Fact]
    public void Ctor_Defaults_Citations_To_Empty_NonNull()
    {
        var response = new ChatResponse(Tokens("x"));

        response.Citations.ShouldNotBeNull();
        response.Citations.ShouldBeEmpty();
    }

    [Fact]
    public async Task FromText_Yields_Single_Element_Sequence()
    {
        var response = ChatResponse.FromText("complete answer");

        var collected = new List<string>();
        await foreach (var token in response.Tokens)
        {
            collected.Add(token);
        }

        collected.ShouldHaveSingleItem();
        collected[0].ShouldBe("complete answer");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromText_Throws_When_Text_Null_Or_Whitespace(string? text)
    {
        Should.Throw<ArgumentException>(() => ChatResponse.FromText(text!))
            .Message.ShouldContain("Text");
    }

    [Fact]
    public void FromText_Carries_Citations()
    {
        var response = ChatResponse.FromText("x", new[] { new Citation("a") });

        response.Citations.Count.ShouldBe(1);
    }
}
