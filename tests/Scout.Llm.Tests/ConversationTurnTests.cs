using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// Contract tests for the <see cref="ConversationTurn"/> replay primitive and the
/// <see cref="ChatRequest.History"/> default.
/// </summary>
public sealed class ConversationTurnTests
{
    [Fact]
    public void ConversationTurn_Equality_Is_Value_Based()
    {
        var a = new ConversationTurn(ConversationRole.User, "hi");
        var b = new ConversationTurn(ConversationRole.User, "hi");

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void ConversationTurn_Inequality_On_Role_Or_Text()
    {
        var baseTurn = new ConversationTurn(ConversationRole.User, "hi");

        baseTurn.ShouldNotBe(new ConversationTurn(ConversationRole.Assistant, "hi"));
        baseTurn.ShouldNotBe(new ConversationTurn(ConversationRole.User, "bye"));
    }

    [Fact]
    public void ChatRequest_History_Defaults_Empty_And_NonNull()
    {
        var request = new ChatRequest { UserMessage = "q" };

        request.History.ShouldNotBeNull();
        request.History.ShouldBeEmpty();
    }
}
