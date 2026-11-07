using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Chat adapter for the owner's own machine: the desktop app writes one JSON
/// request per line to the worker's stdin (<c>ask</c> with text or a WAV
/// recording, or <c>cancel</c>) and reads the reply as <c>SCOUT_LOCAL</c> lines
/// on stdout. <c>read</c> with a screenshot is answered with one <c>text</c>
/// event holding the text on it, on its own, beside any ask or live session. <c>listen</c>, <c>audio</c> and <c>stop</c> run up to two live
/// transcription sessions at once, each under its own id, answered with
/// <c>partial</c> and numbered <c>final</c> text, a <c>translation</c> of each final when the listen asks for one,
/// and a <c>complete</c> after the stop. <c>keep</c>, <c>meetings</c>, <c>export</c> and <c>delete</c> store, list,
/// render as Markdown and delete the meetings the owner chose to keep, each on its own. Every request is a message from the local owner in one stable
/// channel, so memory continues across quick asks until a <c>reset</c> line clears it. An ask's optional <c>context</c> goes to the
/// model after the question and stays out of that memory. A malformed or oversized line
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
    internal const string NoLiveModelReply =
        "Live transcription needs a Whisper model. Download one in the Scout app under AI Sources, Local Models.";
    internal const string AlreadyListeningReply = "Scout is already listening.";
    internal const string LiveFailedReply = "Scout could not transcribe that; see Scout logs.";
    internal const string ImageRefusedReply = "The screenshot must be a JPEG of at most 3 MB.";
    internal const string NoReaderReply = "Scout cannot read text from the screen on this machine; the text reader is not installed.";
    internal const string ReadFailedReply = "Scout could not read the text on the screen; see Scout logs.";
    internal const string NoMeetingsReply = "Scout keeps no meetings on this machine; the meetings database is not configured.";
    internal const string NoMeetingReply = "Scout has no meeting with that id.";
    internal const string MeetingLimitsReply = "A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters.";
    internal const string MeetingFailedReply = "Scout could not save or read the meeting; see Scout logs.";
    internal const int DefaultMeetingsLimit = 100;
    internal const int MaxMeetingsLimit = 1000;
    internal const int MaxImageBytes = 3 * 1024 * 1024;
    internal const int MaxLiveSessions = 2;

    private const int IdProbeChars = 256;
    private const int ReusableLineChars = 64 * 1024;
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TranslationDrainTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<Stream> _openInput;
    private readonly LocalEventWriter _writer;
    private readonly ILogger<LocalChatAdapter> _logger;
    private readonly ILiveTranscriber? _live;
    private readonly IScreenTextReader? _reader;
    private readonly ILiveTranslator? _translator;
    private readonly Func<CancellationToken, Task<string?>>? _mandateLanguage;
    private readonly IMeetingStore? _meetings;
    private readonly Func<MeetingRecord, string>? _renderMeeting;
    private readonly IConversationMemoryStore? _memory;
    private readonly IConversationKeyProvider? _keys;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _conversations = new();
    private readonly ConcurrentDictionary<string, Listening> _sessions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime;
    private long _nextConversation;

    /// <summary>
    /// Creates the adapter over the process's stdin and stdout; without
    /// <paramref name="live"/> every <c>listen</c> is refused, without
    /// <paramref name="reader"/> every <c>read</c>, and without <paramref name="translator"/> a listen that asks
    /// for a translation is transcribed untranslated. <paramref name="mandateLanguage"/> answers the channel
    /// mandate's language for a listen that asks for <c>auto</c>, and without <paramref name="meetings"/> and
    /// <paramref name="renderMeeting"/>, which writes a kept meeting as Markdown, every meeting request is refused.
    /// A <c>reset</c> clears the local conversation in <paramref name="memory"/> under the key <paramref name="keys"/> derives.
    /// </summary>
    public LocalChatAdapter(
        ILogger<LocalChatAdapter> logger,
        ILiveTranscriber? live = null,
        IScreenTextReader? reader = null,
        ILiveTranslator? translator = null,
        Func<CancellationToken, Task<string?>>? mandateLanguage = null,
        IMeetingStore? meetings = null,
        Func<MeetingRecord, string>? renderMeeting = null,
        IConversationMemoryStore? memory = null,
        IConversationKeyProvider? keys = null)
        : this(Console.OpenStandardInput, Console.Out, logger, live, reader, translator, mandateLanguage, meetings, renderMeeting, memory, keys)
    {
    }

    internal LocalChatAdapter(
        Func<Stream> openInput,
        TextWriter output,
        ILogger<LocalChatAdapter> logger,
        ILiveTranscriber? live = null,
        IScreenTextReader? reader = null,
        ILiveTranslator? translator = null,
        Func<CancellationToken, Task<string?>>? mandateLanguage = null,
        IMeetingStore? meetings = null,
        Func<MeetingRecord, string>? renderMeeting = null,
        IConversationMemoryStore? memory = null,
        IConversationKeyProvider? keys = null)
    {
        _openInput = openInput ?? throw new ArgumentNullException(nameof(openInput));
        _writer = new LocalEventWriter(output ?? throw new ArgumentNullException(nameof(output)));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _live = live;
        _reader = reader;
        _translator = translator;
        _mandateLanguage = mandateLanguage;
        _meetings = meetings;
        _renderMeeting = renderMeeting;
        _memory = memory;
        _keys = keys;
    }

    /// <inheritdoc />
    public string Name => "Local";

    /// <summary>Completes when stdin reaches end of file or can no longer be read.</summary>
    public Task InputClosed => _inputClosed.Task;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("\"id\"\\s*:\\s*\"([A-Za-z0-9-]{1,64})\"", RegexOptions.CultureInvariant)]
    private static partial Regex IdProbe();

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex MeetingIdPattern();

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
                    await TryDispatchAsync(line, tooLarge, handler, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask TryDispatchAsync(
        StringBuilder line,
        bool tooLarge,
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(line, tooLarge, handler, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask DispatchAsync(
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

        if (request is { Type: "reset" })
        {
            Reset();
            return;
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
            case "ask" when request.Image is { } attached && !IsAcceptedImage(attached):
                _logger.LogWarning("Refused local request {Id}: the screenshot is not a JPEG of at most 3 MB.", id);
                Refuse(id, ImageRefusedReply);
                return;
            case "ask" when ToMessage(request) is { } message:
                Start(
                    id,
                    request.Image?.Base64 is { } jpeg ? message with { Image = new ImageAttachment("image/jpeg", jpeg) } : message,
                    handler,
                    cancellationToken);
                return;
            case "listen":
                Listen(id, request.Translate, cancellationToken);
                return;
            case "audio" when _sessions.TryGetValue(id, out var feeding) && request.Pcm16 is { Length: > 0 } pcm16:
                await FeedAsync(feeding, pcm16, cancellationToken).ConfigureAwait(false);
                return;
            case "stop" when _sessions.TryRemove(id, out var stopping):
                Track(() => StopListeningAsync(stopping, cancellationToken));
                return;
            case "read":
                Read(id, request.Image, cancellationToken);
                return;
            case "keep" or "meetings" or "export" or "delete" when _meetings is null || _renderMeeting is null:
                _logger.LogInformation("Refused local {Type} {Id}: no meetings database is configured.", request.Type, id);
                Refuse(id, NoMeetingsReply);
                return;
            case "keep" when ToMeeting(request.Meeting) is { } meeting:
                _logger.LogInformation(
                    "Local keep {Id} accepted (lines={Lines}, turns={Turns}).", id, meeting.Lines.Count, meeting.Turns.Count);
                Track(() => KeepAsync(id, _meetings!, meeting, cancellationToken));
                return;
            case "meetings":
                Track(() => ListMeetingsAsync(
                    id, _meetings!, Math.Clamp(request.Limit ?? DefaultMeetingsLimit, 1, MaxMeetingsLimit), cancellationToken));
                return;
            case "export" when request.MeetingId is { } exported:
                Track(() => ExportAsync(id, _meetings!, _renderMeeting!, exported, cancellationToken));
                return;
            case "delete" when request.All is true:
                Track(() => DeleteAllMeetingsAsync(id, _meetings!, cancellationToken));
                return;
            case "delete" when request.MeetingId is { } deleted:
                Track(() => DeleteMeetingAsync(id, _meetings!, deleted, cancellationToken));
                return;
            default:
                _logger.LogWarning("Refused local request {Id}: unknown type or no question.", id);
                Refuse(id, MalformedReply);
                return;
        }
    }

    private void Reset()
    {
        if (_memory is null || _keys is null)
        {
            _logger.LogInformation("Local reset ignored: no conversation memory is configured.");
            return;
        }

        _memory.Clear(_keys.DeriveKey(new ChatMessage(OwnerUserId, ChannelId, "reset")));
        _logger.LogInformation("Local conversation memory cleared.");
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

    private static bool IsAcceptedImage(LocalImage image)
        => string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            && image.Base64 is { Length: > 0 and <= MaxImageBytes };

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
            { IsLocalOwner = true, OriginReference = LocalOriginPoster.Reference };
        }

        return string.IsNullOrWhiteSpace(request.Text)
            ? null
            : new ChatMessage(OwnerUserId, ChannelId, request.Text)
            {
                IsLocalOwner = true,
                OriginReference = LocalOriginPoster.Reference,
                Context = string.IsNullOrEmpty(request.Context) ? null : request.Context,
            };
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
            "Local request {Id} accepted (textLen={Length}, voice={Voice}, image={Image}, imageBytes={ImageBytes}); dispatching to conversation.",
            id, message.Text.Length, message.Audio is not null, message.Image is not null, message.Image?.Bytes.Length ?? 0);
        Track(() => RunAsync(id, message, handler, request));
    }

    private void Track(Func<Task> work)
    {
        var key = Interlocked.Increment(ref _nextConversation);
        var running = Task.Run(work, CancellationToken.None);
        _conversations[key] = running;
        _ = running.ContinueWith(
            _ => _conversations.TryRemove(key, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Listen(string id, string? translate, CancellationToken cancellationToken)
    {
        if (_live is null)
        {
            _logger.LogInformation("Refused live session {Id}: no Whisper model is installed.", id);
            Refuse(id, NoLiveModelReply);
            return;
        }
        if (_sessions.ContainsKey(id))
        {
            _logger.LogInformation("Refused live session {Id}: it is already listening; {Open} live session(s) open.", id, _sessions.Count);
            Refuse(id, AlreadyListeningReply);
            return;
        }
        if (_sessions.Count >= MaxLiveSessions)
        {
            _logger.LogInformation("Refused live session {Id}: {Open} live sessions are already open.", id, _sessions.Count);
            Refuse(id, AlreadyListeningReply);
            return;
        }

        var translation = string.IsNullOrWhiteSpace(translate)
            ? null
            : new LiveTranslationQueue(
                id,
                _translator,
                TargetLanguageAsync(id, translate.Trim(), cancellationToken),
                line => _writer.WriteTranslation(id, line),
                TimeProvider.System,
                _logger,
                cancellationToken);
        var seq = 0;
        var session = _live.Start(
            transcript =>
            {
                if (!transcript.Final)
                {
                    _writer.Write(id, "partial", transcript.Text);
                    return Task.CompletedTask;
                }

                seq++;
                _writer.Write(id, "final", transcript.Text, seq: seq);
                translation?.Enqueue(seq, transcript.Text);
                return Task.CompletedTask;
            },
            cancellationToken);
        _sessions[id] = new Listening(id, session, translation);
        _logger.LogInformation(
            "Live session {Id} started (translate={Translate}); {Open} live session(s) open.",
            id, translation is not null, _sessions.Count);
    }

    private async Task<string> TargetLanguageAsync(string id, string translate, CancellationToken cancellationToken)
    {
        var interfaceLanguage = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "en";
        if (!string.Equals(translate, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return translate;
        }
        if (_mandateLanguage is null)
        {
            return interfaceLanguage;
        }

        try
        {
            var mandate = await _mandateLanguage(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(mandate) ? interfaceLanguage : mandate.Trim();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Live session {Id} could not read the mandate language ({Error}); translating into the interface language.",
                id, ex.GetType().Name);
            return interfaceLanguage;
        }
    }

    private async Task FeedAsync(Listening listening, byte[] pcm16, CancellationToken cancellationToken)
    {
        try
        {
            await listening.Session.FeedAsync(pcm16, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Live session {Id} received {Bytes} bytes.", listening.Id, pcm16.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(listening.Id, out _);
            _logger.LogError(
                ex, "Live session {Id} failed while receiving audio; {Open} live session(s) open.", listening.Id, _sessions.Count);
            if (listening.Translation is { } translation)
            {
                await translation.DrainAsync(TimeSpan.Zero).ConfigureAwait(false);
            }
            Refuse(listening.Id, LiveFailedReply);
        }
    }

    private async Task StopListeningAsync(Listening listening, CancellationToken cancellationToken)
    {
        try
        {
            await listening.Session.StopAsync(cancellationToken).ConfigureAwait(false);
            if (listening.Translation is { } translation)
            {
                await translation.DrainAsync(TranslationDrainTimeout).ConfigureAwait(false);
            }
            _writer.Write(listening.Id, "complete");
            _logger.LogInformation("Live session {Id} stopped; {Open} live session(s) open.", listening.Id, _sessions.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Live session {Id} ended with the adapter.", listening.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live session {Id} failed.", listening.Id);
            if (listening.Translation is { } translation)
            {
                await translation.DrainAsync(TimeSpan.Zero).ConfigureAwait(false);
            }
            Refuse(listening.Id, LiveFailedReply);
        }
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

    private void Read(string id, LocalImage? image, CancellationToken cancellationToken)
    {
        if (image is null || !IsAcceptedImage(image))
        {
            _logger.LogWarning("Refused local read {Id}: the screenshot is not a JPEG of at most 3 MB.", id);
            Refuse(id, ImageRefusedReply);
            return;
        }
        if (_reader is null)
        {
            _logger.LogInformation("Refused local read {Id}: no text reader is installed.", id);
            Refuse(id, NoReaderReply);
            return;
        }

        var jpeg = image.Base64!;
        _logger.LogInformation("Local read {Id} accepted (imageBytes={ImageBytes}).", id, jpeg.Length);
        Track(() => ReadTextAsync(id, _reader, jpeg, cancellationToken));
    }

    private async Task ReadTextAsync(string id, IScreenTextReader reader, byte[] jpeg, CancellationToken cancellationToken)
    {
        try
        {
            var text = await reader.ReadAsync(jpeg, cancellationToken).ConfigureAwait(false);
            _writer.Write(id, "text", text);
            _logger.LogInformation("Local read {Id} answered (imageBytes={ImageBytes}, textLen={Length}).", id, jpeg.Length, text.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Local read {Id} ended with the adapter.", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local read {Id} failed (imageBytes={ImageBytes}).", id, jpeg.Length);
            Refuse(id, ReadFailedReply);
        }
    }

    private static MeetingRecord? ToMeeting(LocalMeeting? meeting)
    {
        if (meeting is not { StartedAt: { } startedAt, EndedAt: { } endedAt })
        {
            return null;
        }

        var lines = new List<MeetingLine>(meeting.Lines?.Count ?? 0);
        foreach (var line in meeting.Lines ?? [])
        {
            if (line is not { At: { } at, Lane: "me" or "others", Text: { } text })
            {
                return null;
            }
            lines.Add(new MeetingLine(at, line.Lane, text, line.Translation, line.Language));
        }

        var turns = new List<MeetingTurn>(meeting.Turns?.Count ?? 0);
        foreach (var turn in meeting.Turns ?? [])
        {
            if (turn is not { At: { } at, Question: { } question, Answer: { } answer })
            {
                return null;
            }
            turns.Add(new MeetingTurn(at, question, answer, turn.Screenshot, turn.Transcript, turn.ScreenText));
        }

        return new MeetingRecord(null, startedAt, endedAt, meeting.Title ?? string.Empty, lines, turns);
    }

    private async Task KeepAsync(string id, IMeetingStore store, MeetingRecord meeting, CancellationToken cancellationToken)
    {
        try
        {
            var meetingId = await store.KeepAsync(meeting, cancellationToken).ConfigureAwait(false);
            _writer.WriteMeetingEvent(id, "kept", meetingId);
            _logger.LogInformation(
                "Local keep {Id} stored meeting {MeetingId} (lines={Lines}, turns={Turns}).",
                id, meetingId, meeting.Lines.Count, meeting.Turns.Count);
        }
        catch (ArgumentException)
        {
            _logger.LogInformation(
                "Refused local keep {Id}: past the meeting limits (lines={Lines}, turns={Turns}, titleLen={TitleLength}).",
                id, meeting.Lines.Count, meeting.Turns.Count, meeting.Title.Length);
            Refuse(id, MeetingLimitsReply);
        }
        catch (Exception ex)
        {
            MeetingFailed("keep", id, ex, cancellationToken);
        }
    }

    private async Task ListMeetingsAsync(string id, IMeetingStore store, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var items = await store.ListAsync(limit, cancellationToken).ConfigureAwait(false);
            _writer.WriteMeetingEvent(id, "meetings", items: items);
            _logger.LogInformation("Local meetings {Id} answered {Count} meeting(s) (limit={Limit}).", id, items.Count, limit);
        }
        catch (Exception ex)
        {
            MeetingFailed("meetings", id, ex, cancellationToken);
        }
    }

    private async Task ExportAsync(
        string id, IMeetingStore store, Func<MeetingRecord, string> render, string meetingId, CancellationToken cancellationToken)
    {
        try
        {
            var meeting = MeetingIdPattern().IsMatch(meetingId)
                ? await store.GetAsync(meetingId, cancellationToken).ConfigureAwait(false)
                : null;
            if (meeting is null)
            {
                _logger.LogInformation("Refused local export {Id}: no such meeting.", id);
                Refuse(id, NoMeetingReply);
                return;
            }

            var markdown = render(meeting);
            _writer.WriteMeetingEvent(id, "markdown", meetingId, markdown);
            _logger.LogInformation(
                "Local export {Id} answered meeting {MeetingId} (lines={Lines}, turns={Turns}, textLen={Length}).",
                id, meetingId, meeting.Lines.Count, meeting.Turns.Count, markdown.Length);
        }
        catch (Exception ex)
        {
            MeetingFailed("export", id, ex, cancellationToken);
        }
    }

    private async Task DeleteMeetingAsync(string id, IMeetingStore store, string meetingId, CancellationToken cancellationToken)
    {
        try
        {
            if (!MeetingIdPattern().IsMatch(meetingId) || !await store.DeleteAsync(meetingId, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Refused local delete {Id}: no such meeting.", id);
                Refuse(id, NoMeetingReply);
                return;
            }

            _writer.WriteMeetingEvent(id, "deleted", count: 1);
            _logger.LogInformation("Local delete {Id} deleted meeting {MeetingId}.", id, meetingId);
        }
        catch (Exception ex)
        {
            MeetingFailed("delete", id, ex, cancellationToken);
        }
    }

    private async Task DeleteAllMeetingsAsync(string id, IMeetingStore store, CancellationToken cancellationToken)
    {
        try
        {
            var count = (await store.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false)).Count;
            await store.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
            _writer.WriteMeetingEvent(id, "deleted", count: count);
            _logger.LogInformation("Local delete {Id} deleted all {Count} meeting(s).", id, count);
        }
        catch (Exception ex)
        {
            MeetingFailed("delete", id, ex, cancellationToken);
        }
    }

    private void MeetingFailed(string type, string id, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Local {Type} {Id} ended with the adapter.", type, id);
            return;
        }

        _logger.LogError("Local {Type} {Id} failed ({Error}).", type, id, ex.GetType().Name);
        Refuse(id, MeetingFailedReply);
    }

    internal LocalEventWriter? RunningWriter => Volatile.Read(ref _lifetime) is null ? null : _writer;

    internal ILiveTranscriber? LiveTranscriber => _live;

    internal IScreenTextReader? ScreenTextReader => _reader;

    internal ILiveTranslator? Translator => _translator;

    internal Func<CancellationToken, Task<string?>>? MandateLanguage => _mandateLanguage;

    internal IMeetingStore? Meetings => _meetings;

    internal Func<MeetingRecord, string>? MeetingRenderer => _renderMeeting;

    internal IConversationMemoryStore? MemoryStore => _memory;

    internal IConversationKeyProvider? KeyProvider => _keys;

    private sealed record LocalRequest(
        string? Type,
        string? Id,
        string? Text,
        LocalAudio? Audio,
        byte[]? Pcm16,
        LocalImage? Image,
        string? Translate,
        LocalMeeting? Meeting = null,
        string? MeetingId = null,
        bool? All = null,
        int? Limit = null,
        string? Context = null);

    private sealed record LocalMeeting(
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        string? Title,
        IReadOnlyList<LocalMeetingLine?>? Lines,
        IReadOnlyList<LocalMeetingTurn?>? Turns);

    private sealed record LocalMeetingLine(DateTimeOffset? At, string? Lane, string? Text, string? Translation, string? Language);

    private sealed record LocalMeetingTurn(
        DateTimeOffset? At, string? Question, string? Answer, bool Screenshot, bool Transcript, bool ScreenText);

    private sealed record Listening(string Id, ILiveSession Session, LiveTranslationQueue? Translation);

    private sealed record LocalAudio(string? ContentType, byte[]? Base64);

    private sealed record LocalImage(string? ContentType, byte[]? Base64);
}
