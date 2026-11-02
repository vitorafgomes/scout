using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Chat adapter for the owner's own machine: the desktop app writes one JSON
/// request per line to the worker's stdin (<c>ask</c> with text or a WAV
/// recording, or <c>cancel</c>) and reads the reply as <c>SCOUT_LOCAL</c> lines
/// on stdout. Every request is a message from the local owner in one stable
/// channel, so memory continues across quick asks. A malformed or oversized line
/// gets a <c>fail</c> event and the reader moves on to the next one.
/// <see cref="InputClosed"/> completes when stdin reaches end of file, which
/// means the desktop app is gone.
/// </summary>
public sealed partial class LocalChatAdapter : IChatAdapter, IAsyncDisposable
{
    /// <summary>The user id of every local message.</summary>
    public const string OwnerUserId = "local-owner";

    /// <summary>The channel id of every local message.</summary>
    public const string ChannelId = "local";

    internal const int MaxRequestChars = 20 * 1024 * 1024;
    internal const string MalformedReply = "Scout could not read that request.";
    internal const string TooLargeReply = "That request is too large for Scout.";
    internal const string DuplicateReply = "That request is already running.";
    internal const string CancelledReply = "Cancelled.";

    private const int IdProbeChars = 256;
    private const int ReusableLineChars = 64 * 1024;
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<Stream> _openInput;
    private readonly LocalEventWriter _writer;
    private readonly ILogger<LocalChatAdapter> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _conversations = new();
    private readonly TaskCompletionSource _inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime;
    private long _nextConversation;

    /// <summary>Creates the adapter over the process's stdin and stdout.</summary>
    public LocalChatAdapter(ILogger<LocalChatAdapter> logger)
        : this(Console.OpenStandardInput, Console.Out, logger)
    {
    }

    internal LocalChatAdapter(Func<Stream> openInput, TextWriter output, ILogger<LocalChatAdapter> logger)
    {
        _openInput = openInput ?? throw new ArgumentNullException(nameof(openInput));
        _writer = new LocalEventWriter(output ?? throw new ArgumentNullException(nameof(output)));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Local";

    /// <summary>Completes when stdin reaches end of file or can no longer be read.</summary>
    public Task InputClosed => _inputClosed.Task;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("\"id\"\\s*:\\s*\"([A-Za-z0-9-]{1,64})\"", RegexOptions.CultureInvariant)]
    private static partial Regex IdProbe();

    /// <inheritdoc />
    public Task StartAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_lifetime is not null)
        {
            throw new InvalidOperationException("The local adapter is already started.");
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var input = _openInput();
        var token = _lifetime.Token;
        _ = Task.Run(() => ReadLoopAsync(input, handler, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_conversations.Values)
                .WaitAsync(InFlightDrainTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Local adapter stopping with {Count} request(s) still running.", _conversations.Count);
        }
        lifetime.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task ReadLoopAsync(
        Stream input,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var tooLarge = false;
        try
        {
            using var reader = new StreamReader(input, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var buffer = new char[64 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != '\n')
                    {
                        continue;
                    }
                    Append(line, buffer.AsSpan(start, i - start), ref tooLarge);
                    TryDispatch(line, tooLarge, handler, cancellationToken);
                    line = line.Capacity > ReusableLineChars ? new StringBuilder() : line.Clear();
                    tooLarge = false;
                    start = i + 1;
                }
                Append(line, buffer.AsSpan(start, read - start), ref tooLarge);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local requests can no longer be read from stdin.");
        }
        finally
        {
            _inputClosed.TrySetResult();
        }
    }

    private void TryDispatch(
        StringBuilder line,
        bool tooLarge,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            Dispatch(line, tooLarge, handler, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A local request failed on the reader; reading continues.");
        }
    }

    private static void Append(StringBuilder line, ReadOnlySpan<char> chars, ref bool tooLarge)
    {
        if (tooLarge)
        {
            return;
        }
        if (line.Length + chars.Length > MaxRequestChars)
        {
            tooLarge = true;
            return;
        }
        line.Append(chars);
    }

    private void Dispatch(
        StringBuilder line,
        bool tooLarge,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var probe = line.ToString(0, Math.Min(line.Length, IdProbeChars));
        var probedId = IdProbe().Match(probe) is { Success: true } m ? m.Groups[1].Value : null;
        if (tooLarge)
        {
            _logger.LogWarning("Refused a local request over {Max} characters.", MaxRequestChars);
            Refuse(probedId, TooLargeReply);
            return;
        }

        var text = line.ToString().TrimEnd('\r');
        if (text.Length == 0)
        {
            return;
        }

        LocalRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LocalRequest>(text, Json);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request?.Id is not { } id || !IdPattern().IsMatch(id))
        {
            _logger.LogWarning("Refused a local request that is not valid JSON or has no valid id.");
            Refuse(probedId, MalformedReply);
            return;
        }

        switch (request.Type)
        {
            case "cancel":
                if (_inFlight.TryGetValue(id, out var running))
                {
                    running.Cancel();
                }
                return;
            case "ask" when ToMessage(request) is { } message:
                Start(id, message, handler, cancellationToken);
                return;
            default:
                _logger.LogWarning("Refused local request {Id}: unknown type or no question.", id);
                Refuse(id, MalformedReply);
                return;
        }
    }

    private void Refuse(string? id, string text)
    {
        try
        {
            _writer.Write(id, "fail", text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write a local refusal to stdout.");
        }
    }

    private static ChatMessage? ToMessage(LocalRequest request)
    {
        if (request.Audio is { } audio)
        {
            if (!string.Equals(audio.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase)
                || audio.Base64 is not { Length: > 0 } bytes)
            {
                return null;
            }

            return new ChatMessage(
                OwnerUserId,
                ChannelId,
                string.Empty,
                Audio: new AudioAttachment(
                    "audio/wav",
                    bytes.Length,
                    null,
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))))
            { IsLocalOwner = true };
        }

        return string.IsNullOrWhiteSpace(request.Text)
            ? null
            : new ChatMessage(OwnerUserId, ChannelId, request.Text) { IsLocalOwner = true };
    }

    private void Start(
        string id,
        ChatMessage message,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_inFlight.TryAdd(id, request))
        {
            request.Dispose();
            Refuse(id, DuplicateReply);
            return;
        }

        _logger.LogInformation(
            "Local request {Id} accepted (textLen={Length}, voice={Voice}); dispatching to conversation.",
            id, message.Text.Length, message.Audio is not null);
        var key = Interlocked.Increment(ref _nextConversation);
        var conversation = Task.Run(() => RunAsync(id, message, handler, request), CancellationToken.None);
        _conversations[key] = conversation;
        _ = conversation.ContinueWith(
            _ => _conversations.TryRemove(key, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunAsync(
        string id,
        ChatMessage message,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationTokenSource request)
    {
        var sink = new LocalResponseSink(id, _writer);
        try
        {
            await handler(message, sink, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            _logger.LogInformation("Local request {Id} cancelled.", id);
            try
            {
                sink.TryFail(CancelledReply);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not report the cancellation of local request {Id}.", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local request {Id} failed.", id);
            try
            {
                sink.TryFail(LocalResponseSink.FailedReply);
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx, "Could not report the failure of local request {Id}.", id);
            }
        }
        finally
        {
            _inFlight.TryRemove(id, out _);
            request.Dispose();
        }
    }

    private sealed record LocalRequest(string? Type, string? Id, string? Text, LocalAudio? Audio);

    private sealed record LocalAudio(string? ContentType, byte[]? Base64);
}
