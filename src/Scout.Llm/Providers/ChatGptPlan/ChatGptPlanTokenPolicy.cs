using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Providers.ChatGptPlan;

/// <summary>
/// Sets the Bearer header of every Responses API request from the token file the
/// desktop writes after ChatGPT sign-in, so a rotated token is used without a
/// restart. A missing, unreadable or expired file, or an HTTP 401, fails with
/// <see cref="LlmSignInRequiredException"/>; no message ever carries the token or the file contents.
/// </summary>
internal sealed class ChatGptPlanTokenPolicy : AuthenticationPolicy
{
    private static readonly TimeSpan ReadAheadOfExpiry = TimeSpan.FromSeconds(60);

    private readonly string _tokenFile;
    private readonly Func<string, string> _readAllText;
    private volatile CachedToken? _cached;

    public ChatGptPlanTokenPolicy(string tokenFile, Func<string, string>? readAllText = null)
    {
        _tokenFile = tokenFile;
        _readAllText = readAllText ?? File.ReadAllText;
    }

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Authorize(message);
        ProcessNext(message, pipeline, currentIndex);
        ThrowWhenRejected(message);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Authorize(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        ThrowWhenRejected(message);
    }

    private void Authorize(PipelineMessage message)
        => message.Request.Headers.Set("Authorization", "Bearer " + CurrentToken());

    private static void ThrowWhenRejected(PipelineMessage message)
    {
        if (message.Response?.Status == 401)
        {
            throw new LlmSignInRequiredException();
        }
    }

    private string CurrentToken()
    {
        var writtenAt = File.GetLastWriteTimeUtc(_tokenFile);
        var now = DateTimeOffset.UtcNow;
        var cached = _cached;
        if (cached is not null
            && cached.WrittenAtUtc == writtenAt
            && cached.ExpiresAt - now > ReadAheadOfExpiry)
        {
            return cached.AccessToken;
        }

        var fresh = Read(writtenAt, cached, now);
        if (fresh.ExpiresAt <= now)
        {
            throw new LlmSignInRequiredException();
        }

        _cached = fresh;
        return fresh.AccessToken;
    }

    private CachedToken Read(DateTime writtenAt, CachedToken? cached, DateTimeOffset now)
    {
        string json;
        try
        {
            json = _readAllText(_tokenFile);
        }
        catch (IOException) when (cached is not null && cached.ExpiresAt > now)
        {
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LlmSignInRequiredException(ex);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(token.GetString())
                && root.TryGetProperty("expires_at", out var expires)
                && expires.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    expires.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var expiresAt))
            {
                return new CachedToken(token.GetString()!, expiresAt, writtenAt);
            }
        }
        catch (JsonException)
        {
        }

        throw new LlmSignInRequiredException();
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt, DateTime WrittenAtUtc);
}
