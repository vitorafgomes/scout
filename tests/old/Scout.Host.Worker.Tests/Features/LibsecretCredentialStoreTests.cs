using Microsoft.Extensions.Logging.Abstractions;
using Scout.Host.Worker.Features;

namespace Scout.Host.Worker.Tests.Features;

/// <summary>
/// Gated on the <c>SCOUT_RUN_REAL_KEYRING_TESTS=1</c> environment
/// variable so CI without libsecret installed does not fail. Runs the
/// real <c>secret-tool</c> binary against the calling user's keyring;
/// always cleans up after itself. When the env var is unset the test
/// returns immediately without asserting.
/// </summary>
public sealed class LibsecretCredentialStoreTests
{
    private static bool ShouldRun =>
        OperatingSystem.IsLinux()
        && string.Equals(
            Environment.GetEnvironmentVariable("SCOUT_RUN_REAL_KEYRING_TESTS"),
            "1",
            StringComparison.Ordinal);

    [Fact]
    public async Task RealRoundTrip_StoreReadDelete()
    {
        if (!ShouldRun)
        {
            return;
        }

#pragma warning disable CA1416
        var store = new LibsecretCredentialStore(NullLogger<LibsecretCredentialStore>.Instance);
        if (!store.IsAvailable)
        {
            return;
        }

        var key = $"scout-tests/{Guid.NewGuid():N}";
        try
        {
            var ok = await store.StoreAsync(key, "the-secret", CancellationToken.None);
            ok.ShouldBeTrue();

            var got = await store.GetAsync(key, CancellationToken.None);
            got.ShouldBe("the-secret");
        }
        finally
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning restore CA1416
    }
}
