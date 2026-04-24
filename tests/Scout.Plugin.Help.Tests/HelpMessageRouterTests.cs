using System.Text.RegularExpressions;
using Scout.Core.Chat;
using Scout.Core.Plugins;

namespace Scout.Plugin.Help.Tests;

public class HelpMessageRouterTests
{
    private static ChatMessage Msg(string text) =>
        new(UserId: "u1", ChannelId: "c1", Text: text);

    private static HelpMessageRouter Sut(params IScoutPlugin[] plugins) =>
        new(plugins.Length == 0 ? Array.Empty<IScoutPlugin>() : plugins);

    [Fact]
    public async Task Exact_ping_replies_pong()
    {
        var response = await Sut().RouteAsync(Msg("ping"), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldBe("pong");
    }

    [Fact]
    public async Task Trimmed_ping_replies_pong()
    {
        var response = await Sut().RouteAsync(Msg("  ping  "), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldBe("pong");
    }

    [Fact]
    public async Task Case_insensitive_PING_replies_pong()
    {
        var response = await Sut().RouteAsync(Msg("PING"), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldBe("pong");
    }

    [Fact]
    public async Task Help_lists_builtins_and_registered_plugins()
    {
        var helpItself = Substitute.For<IScoutPlugin>();
        helpItself.Name.Returns(HelpPlugin.PluginName);

        var cloud = Substitute.For<IScoutPlugin>();
        cloud.Name.Returns("CloudStatus");

        var codeQa = Substitute.For<IScoutPlugin>();
        codeQa.Name.Returns("CodeQA");

        var response = await Sut(helpItself, cloud, codeQa)
            .RouteAsync(Msg("help"), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldContain("Scout is online");
        response.Text.ShouldContain("@scout ping");
        response.Text.ShouldContain("@scout help");
        response.Text.ShouldContain("@scout version");
        response.Text.ShouldContain("- CloudStatus");
        response.Text.ShouldContain("- CodeQA");
        // Command list is wrapped in a Discord code block so the monospace
        // font aligns the dash columns; the fence must appear twice.
        response.Text.ShouldContain("```");
        Regex.Matches(response.Text, "```").Count.ShouldBe(2);
        // Help plugin itself must not be rendered as a separate bullet -
        // it is already represented by the three built-in command lines.
        Regex.Matches(response.Text, @"(?m)^- Help$").Count.ShouldBe(0);
    }

    [Fact]
    public async Task Version_replies_with_semver_prefix()
    {
        var response = await Sut().RouteAsync(Msg("version"), CancellationToken.None);

        response.ShouldNotBeNull();
        Regex.IsMatch(response!.Text, @"^Scout worker v\d+\.\d+\.\d+", RegexOptions.IgnoreCase)
            .ShouldBeTrue($"actual: {response.Text}");
    }

    [Fact]
    public async Task Version_overload_uses_injected_version_string()
    {
        var router = new HelpMessageRouter(Array.Empty<IScoutPlugin>(), workerVersion: "9.9.9");

        var response = await router.RouteAsync(Msg("version"), CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldBe("Scout worker v9.9.9");
    }

    [Fact]
    public async Task Substring_pinging_does_not_match_ping()
    {
        var response = await Sut().RouteAsync(Msg("pinging"), CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Mixed_content_hello_scout_is_not_matched()
    {
        var response = await Sut().RouteAsync(Msg("hello scout"), CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Null_message_throws()
    {
        Func<Task> act = () => Sut().RouteAsync(null!, CancellationToken.None);

        await act.ShouldThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_throws_when_plugins_enumerable_is_null()
    {
        Action create = () => new HelpMessageRouter(plugins: null!);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Whitespace_only_content_is_rejected_by_ChatMessage_contract()
    {
        // The ChatMessage contract forbids blank content; the router never
        // receives such a message. Confirm the boundary lives in ChatMessage.
        Action build = () => _ = new ChatMessage(UserId: "u", ChannelId: "c", Text: "   ");

        build.ShouldThrow<ArgumentException>();
    }
}
