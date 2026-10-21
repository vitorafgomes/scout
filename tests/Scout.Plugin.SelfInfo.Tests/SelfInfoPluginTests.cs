using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Scout.Contracts;
using Scout.Plugin.SelfInfo;

namespace Scout.Plugin.SelfInfo.Tests;

/// <summary>
/// Redaction is the key correctness property of this plugin: every function must
/// return a non-empty string, never throw, and never leak the OS username,
/// machine name, absolute paths, network identity, or any secret value. These
/// tests plant fake secrets into configuration and assert they can never surface.
/// </summary>
public sealed class SelfInfoPluginTests
{
    private const string DiscordSecret = "SECRET-abc123";
    private const string GitHubSecret = "ghp_FAKE";

    // Matches a four-octet dotted address (network identity). Scout's own semver
    // is emitted with at most three numeric segments, so it never matches.
    private static readonly Regex IpLikePattern =
        new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.CultureInvariant);

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Chat:Model"] = "qwen2.5",
                ["Scout:Adapters:Discord:BotToken"] = DiscordSecret,
                ["Scout:Plugins:GitHub:Token"] = GitHubSecret,
            })
            .Build();

    // No ambient identity: the plugin resolves to the public tier, so these
    // redaction snapshots pin exactly today's public-safe output.
    private static SelfInfoPlugin Build() =>
        new(Config(), TimeProvider.System, NullScoutRequestContext.Instance, new SelfInfoOptions());

    private static IReadOnlyList<Func<CancellationToken, Task<string>>> Functions(SelfInfoPlugin plugin) =>
        new Func<CancellationToken, Task<string>>[]
        {
            plugin.WhereInstalledAsync,
            plugin.SystemInfoAsync,
            plugin.ResourceUsageAsync,
            plugin.ActiveConfigurationAsync,
            plugin.WorkerStatusAsync,
        };

    [Fact]
    public async Task Every_Function_Returns_NonEmpty_And_Never_Throws()
    {
        var plugin = Build();

        foreach (var call in Functions(plugin))
        {
            var result = string.Empty;
            await Should.NotThrowAsync(async () => result = await call(CancellationToken.None));
            result.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Output_Never_Leaks_Username_MachineName_Paths_Ip_Or_Secrets()
    {
        var plugin = Build();

        foreach (var call in Functions(plugin))
        {
            var output = await call(CancellationToken.None);

            if (!string.IsNullOrEmpty(Environment.UserName))
            {
                output.ShouldNotContain(Environment.UserName);
            }

            if (!string.IsNullOrEmpty(Environment.MachineName))
            {
                output.ShouldNotContain(Environment.MachineName);
            }

            output.ShouldNotContain("/home/");
            output.ShouldNotContain(@"C:\Users\");
            output.ShouldNotContain(DiscordSecret);
            output.ShouldNotContain(GitHubSecret);
            IpLikePattern.IsMatch(output).ShouldBeFalse($"IP-like pattern leaked in: {output}");
        }
    }

    [Fact]
    public async Task Active_Configuration_Names_Provider_Model_And_Adapter_But_Not_Secret()
    {
        var plugin = Build();

        var output = await plugin.ActiveConfigurationAsync(CancellationToken.None);

        output.ShouldContain("Ollama");
        output.ShouldContain("qwen2.5");
        output.ShouldContain("Discord");
        output.ShouldNotContain(DiscordSecret);
    }

    [Fact]
    public async Task System_Info_Reports_Coarse_Facts_Without_Host_Detail()
    {
        var plugin = Build();

        var output = await plugin.SystemInfoAsync(CancellationToken.None);

        output.ShouldContain(".NET");
        if (!string.IsNullOrEmpty(Environment.MachineName))
        {
            output.ShouldNotContain(Environment.MachineName);
        }
    }

    [Fact]
    public async Task Cancellation_Is_Handled_As_A_Clean_Line_Not_A_Throw()
    {
        var plugin = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        foreach (var call in Functions(plugin))
        {
            var result = string.Empty;
            await Should.NotThrowAsync(async () => result = await call(cts.Token));
            result.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
