using Scout.Llm.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// The worker tells the desktop what each answer used, and when the ChatGPT plan hit its usage limit, as one
/// <c>SCOUT_LOCAL</c> line with a null id, the shape the desktop's usage store parses.
/// </summary>
public sealed class StdoutUsageSinkTests
{
    [Fact]
    public void An_Answer_Writes_One_Usage_Line_With_Its_Source_Model_Tokens_And_Cost()
    {
        using var stdout = new StringWriter();

        new StdoutUsageSink(stdout).Answered(new LlmUsage("OpenRouter", "openai/gpt-4o-mini", 10, 5, 15, 0.0012m));

        stdout.ToString().ShouldBe(
            """SCOUT_LOCAL {"id":null,"event":"usage","source":"OpenRouter","model":"openai/gpt-4o-mini","promptTokens":10,"completionTokens":5,"totalTokens":15,"cost":0.0012}"""
            + Environment.NewLine);
    }

    [Fact]
    public void An_Answer_Without_Usage_Writes_A_Usage_Line_With_No_Counts()
    {
        using var stdout = new StringWriter();

        new StdoutUsageSink(stdout).Answered(new LlmUsage("HuggingFace", "meta-llama/Llama-3.1-8B-Instruct", null, null, null, null));

        stdout.ToString().ShouldBe(
            """SCOUT_LOCAL {"id":null,"event":"usage","source":"HuggingFace","model":"meta-llama/Llama-3.1-8B-Instruct"}"""
            + Environment.NewLine);
    }

    [Fact]
    public void The_Plan_Usage_Limit_Writes_One_Usage_Limit_Line_With_Its_Source()
    {
        using var stdout = new StringWriter();

        new StdoutUsageSink(stdout).UsageLimitReached("ChatGptPlan");

        stdout.ToString().ShouldBe(
            """SCOUT_LOCAL {"id":null,"event":"usage-limit","source":"ChatGptPlan"}""" + Environment.NewLine);
    }
}
