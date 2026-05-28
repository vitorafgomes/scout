using System.Net;
using System.Reflection;
using Octokit;

namespace Scout.Plugin.GitHub.Tests.Internal;

/// <summary>
/// Helpers that construct Octokit exception/response instances suitable
/// for tests. Octokit hides most ctors behind <see cref="IResponse"/>,
/// so reflection is the cleanest route for the few we cannot get to
/// via the public surface.
/// </summary>
internal static class OctokitTestDoubles
{
    internal static IResponse Response(
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, string>? headers = null,
        ApiInfo? apiInfo = null)
    {
        var sub = Substitute.For<IResponse>();
        sub.StatusCode.Returns(statusCode);
        sub.Headers.Returns(headers ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
        sub.ApiInfo.Returns(apiInfo ?? BuildApiInfo());
        return sub;
    }

    internal static ApiInfo BuildApiInfo(
        IReadOnlyList<string>? oauthScopes = null,
        RateLimit? rateLimit = null)
    {
        // ApiInfo has a single public ctor; the parameter list is
        // discovered reflectively to remain insensitive to ordering.
        var ctor = typeof(ApiInfo).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(IList<string>) || t == typeof(IReadOnlyList<string>) =>
                (object?)(oauthScopes?.ToList() ?? new List<string>()),
            var t when t == typeof(IDictionary<string, Uri>) => new Dictionary<string, Uri>(),
            var t when t == typeof(string) => string.Empty,
            var t when t == typeof(TimeSpan) => TimeSpan.Zero,
            var t when t == typeof(RateLimit) => rateLimit ?? BuildRateLimit(0, 0, DateTimeOffset.UtcNow),
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();

        return (ApiInfo)ctor.Invoke(args);
    }

    internal static RateLimit BuildRateLimit(int limit, int remaining, DateTimeOffset reset)
    {
        // Use the public (int, int, long) ctor explicitly: the
        // serialization ctor would be the wrong path for tests.
        var ctor = typeof(RateLimit).GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int), typeof(int), typeof(long) },
            modifiers: null) ?? throw new InvalidOperationException(
                "Octokit.RateLimit(int, int, long) ctor not found.");

        return (RateLimit)ctor.Invoke(new object[]
        {
            limit,
            remaining,
            reset.ToUnixTimeSeconds(),
        });
    }

    internal static RateLimitExceededException RateLimitExceeded(DateTimeOffset reset)
    {
        var rate = BuildRateLimit(5000, 0, reset);
        var apiInfo = BuildApiInfo(rateLimit: rate);
        var response = Response(HttpStatusCode.Forbidden, apiInfo: apiInfo);
        return new RateLimitExceededException(response);
    }

    internal static ForbiddenException Forbidden()
    {
        return new ForbiddenException(Response(HttpStatusCode.Forbidden));
    }
}
