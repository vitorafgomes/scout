using Microsoft.Extensions.Logging;
using Scout.Host.ControlPlane.Security;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Security;

public sealed class CallerTokensTests
{
    private const string Desktop = "desktop-token-value";
    private const string Worker = "worker-token-value";

    [Fact]
    public void A_Worker_Token_Equal_To_The_Desktop_Token_Refuses_To_Start_Without_Naming_It()
    {
        var logger = new RecordingLogger<CallerTokens>();

        var ex = Should.Throw<InvalidOperationException>(() =>
            CallerTokens.Create(SessionToken.FromValue(Desktop), Desktop, logger));

        ex.Message.ShouldBe(
            "The control plane's worker token equals its desktop token; refusing to start because the worker would gain the desktop's calls.");
        ex.Message.ShouldNotContain(Desktop);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Missing_Worker_Token_Leaves_The_Worker_Role_Empty_And_Logs_One_Warning(string? worker)
    {
        var logger = new RecordingLogger<CallerTokens>();

        var tokens = CallerTokens.Create(SessionToken.FromValue(Desktop), worker, logger);

        tokens.Worker.ShouldBeNull();
        tokens.Desktop.Matches(Desktop).ShouldBeTrue();
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe(
            "SCOUT_CONTROLPLANE_WORKER_TOKEN is not set; the WorkerGate service refuses every caller, so the worker cannot request a write.");
    }

    [Fact]
    public void A_Distinct_Worker_Token_Matches_Only_Itself_And_Is_Never_Logged()
    {
        var logger = new RecordingLogger<CallerTokens>();

        var tokens = CallerTokens.Create(SessionToken.FromValue(Desktop), Worker, logger);

        tokens.Worker.ShouldNotBeNull().Matches(Worker).ShouldBeTrue();
        tokens.Worker.Matches(Desktop).ShouldBeFalse();
        tokens.Desktop.Matches(Worker).ShouldBeFalse();
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void The_Worker_Env_Var_Name_Is_The_One_The_Ollama_Launch_Scrubs()
    {
        Scout.ControlPlane.Providers.OllamaProviderService.WorkerTokenEnvVarName.ShouldBe(CallerTokens.WorkerEnvVarName);
    }
}
