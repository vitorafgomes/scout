using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Internal;
using ICredentialStore = Scout.Core.Features.ICredentialStore;

namespace Scout.Plugin.GitHub.Tests;

public class GitHubClientFactoryTests
{
    [Fact]
    public async Task CreateAsync_Throws_When_Pat_Missing()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/github/pat", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var sut = BuildSut(store, _ => Substitute.For<IGitHubClient>());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.CreateAsync(CancellationToken.None));
        ex.Message.ShouldContain("PAT credential missing");
    }

    [Fact]
    public async Task CreateAsync_Reads_Pat_From_CredentialStore()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/github/pat", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("ghp_one"));

        var built = Substitute.For<IGitHubClient>();
        var seenPats = new List<string>();
        var sut = BuildSut(store, pat =>
        {
            seenPats.Add(pat);
            return built;
        });

        var client = await sut.CreateAsync(CancellationToken.None);

        client.ShouldBeSameAs(built);
        seenPats.ShouldBe(new[] { "ghp_one" });
    }

    [Fact]
    public async Task CreateAsync_Caches_Client_Within_Ttl()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/github/pat", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("ghp_one"));

        var built = Substitute.For<IGitHubClient>();
        var buildCount = 0;
        var sut = BuildSut(store, _ =>
        {
            buildCount++;
            return built;
        });

        var first = await sut.CreateAsync(CancellationToken.None);
        var second = await sut.CreateAsync(CancellationToken.None);

        first.ShouldBeSameAs(second);
        buildCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreateAsync_Recreates_When_Pat_Rotates()
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("scout/github/pat", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("ghp_one"),
                Task.FromResult<string?>("ghp_two"));

        var buildCount = 0;
        var sut = BuildSut(store, _ =>
        {
            buildCount++;
            return Substitute.For<IGitHubClient>();
        });

        await sut.CreateAsync(CancellationToken.None);
        await sut.CreateAsync(CancellationToken.None);

        buildCount.ShouldBe(2);
    }

    private static GitHubClientFactory BuildSut(
        ICredentialStore store,
        Func<string, IGitHubClient> clientOverride)
    {
        return new GitHubClientFactory(
            store,
            Options.Create(new GitHubPluginOptions()),
            NullLogger<GitHubClientFactory>.Instance,
            clientOverride);
    }
}
