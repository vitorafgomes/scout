using System.ClientModel;
using System.Net;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// The provider failure translation on the branches no other test reaches: which rule wins when a status and an
/// account-refusal code disagree, a failure with no status, and bodies the translation cannot read, which must leave
/// the status rule in charge and never throw.
/// </summary>
public sealed class ProviderFailureTranslationTests
{
    [Theory]
    [InlineData(429, """{"error":{"code":"billing_frozen"}}""", typeof(LlmRateLimitException), null)]
    [InlineData(401, """{"error":{"code":"billing_error"}}""", typeof(LlmQuotaExceededException), "billing_error")]
    [InlineData(401, """{"error":{"type":"organization_on_hold"}}""", typeof(LlmQuotaExceededException), "organization_on_hold")]
    [InlineData(404, """{"error":{"code":"enforced_spend_limit_reached"}}""", typeof(LlmQuotaExceededException), "enforced_spend_limit_reached")]
    [InlineData(404, """{"error":{"type":"invalid_request_error","code":"model_not_found"}}""", typeof(LlmModelNotFoundException), null)]
    [InlineData(429, """{"error":{"type":"insufficient_quota","code":"rate_limit_exceeded"}}""", typeof(LlmQuotaExceededException), "rate_limit_exceeded")]
    [InlineData(402, """{"error":{"type":"insufficient_quota"}}""", typeof(LlmQuotaExceededException), "insufficient_quota")]
    public void A_Status_And_A_Body_Code_Are_Translated_By_The_Rule_That_Comes_First(
        int status, string body, Type expected, string? code)
    {
        var raw = new HttpOperationException((HttpStatusCode)status, body, "provider failure", null);

        var translated = LlmFailures.Translate(raw, "chat-model");

        translated.ShouldBeOfType(expected);
        (translated as LlmQuotaExceededException)?.Code.ShouldBe(code);
        (translated as LlmModelNotFoundException)?.Model.ShouldBe("chat-model");
        translated.InnerException.ShouldBeSameAs(raw, "the provider exception stays the inner exception of a limit or a missing model");
    }

    [Fact]
    public void A_Quota_Code_On_A_Status_That_Is_Not_429_Or_402_Is_Not_A_Quota_Refusal()
    {
        var raw = new HttpOperationException(HttpStatusCode.InternalServerError, """{"error":{"code":"credit_balance_exhausted"}}""", "boom", null);

        LlmFailures.Translate(raw, "chat-model").ShouldBeSameAs(raw);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"error":"overloaded"}""")]
    [InlineData("""{"error":{"code":5,"type":true}}""")]
    [InlineData("not json {")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_Body_That_Cannot_Be_Read_Leaves_The_Status_Rule_In_Charge_And_Never_Throws(string? body)
    {
        var throttled = new HttpOperationException(HttpStatusCode.TooManyRequests, body, "rate", null);
        var payment = new HttpOperationException(HttpStatusCode.PaymentRequired, body, "payment", null);
        var unauthorized = new HttpOperationException(HttpStatusCode.Unauthorized, body, "key", null);
        var missing = new HttpOperationException(HttpStatusCode.NotFound, body, "missing", null);

        LlmFailures.Translate(throttled, "chat-model").ShouldBeOfType<LlmRateLimitException>();
        LlmFailures.Translate(payment, "chat-model").ShouldBeOfType<LlmQuotaExceededException>().Code.ShouldBe("payment_required");
        LlmFailures.Translate(unauthorized, "chat-model").ShouldBeOfType<LlmKeyRejectedException>().InnerException.ShouldBeNull();
        LlmFailures.Translate(missing, "chat-model").ShouldBeSameAs(missing);
    }

    [Fact]
    public void A_Failure_With_No_Status_Is_A_Quota_Refusal_Only_When_Its_Body_Names_An_Account_Refusal()
    {
        var refused = new HttpOperationException("refused") { ResponseContent = """{"error":{"code":"billing_frozen"}}""" };
        var bare = new HttpOperationException("no status");
        var noResponse = new ClientResultException("no response");
        var other = new InvalidOperationException("provider down");

        LlmFailures.Translate(refused, "chat-model").ShouldBeOfType<LlmQuotaExceededException>().Code.ShouldBe("billing_frozen");
        LlmFailures.Translate(bare, "chat-model").ShouldBeSameAs(bare);
        LlmFailures.Translate(noResponse, "chat-model").ShouldBeSameAs(noResponse);
        LlmFailures.Translate(other, "chat-model").ShouldBeSameAs(other);
    }

    [Theory]
    [InlineData(403, """{"error":{"code":"billing_frozen","type":"invalid_request_error"}}""", "billing_frozen")]
    [InlineData(400, """{"error":{"code":null,"type":"billing_error"}}""", "billing_error")]
    public async Task An_Account_Refusal_From_The_Streaming_OpenAI_Connector_Is_A_Quota_Refusal_With_Its_Code(
        int status, string body, string code)
    {
        using var http = new HttpClient(new Canned((HttpStatusCode)status, body));
        var kernel = Kernel.CreateBuilder().AddOpenAIChatCompletion("chat-model", "test-key", httpClient: http).Build();
        var gateway = new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel);

        var refused = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        refused.Code.ShouldBe(code);
        refused.InnerException.ShouldBeOfType<ClientResultException>().Status.ShouldBe(status);
    }

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
