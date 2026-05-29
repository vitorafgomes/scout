using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Teams.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams;

/// <summary>
/// Teams implementation of the inverted <see cref="IChatAdapter"/>.
///
/// WRINKLE: Teams is webhook-push, not connect-and-listen. Discord/Slack open a
/// socket and pull events; Teams has no outbound connection at all. The Azure Bot
/// Service relays each inbound activity as an HTTPS POST to a messaging endpoint
/// the host exposes (e.g. <c>POST /api/messages</c>). There is therefore nothing
/// to "connect" in <see cref="StartAsync"/>.
///
/// Live hosting (NOT exercised here — needs real Azure):
/// <list type="bullet">
///   <item>Register a bot in Azure (App Id + password / managed identity) and set
///   its messaging endpoint to the host's HTTPS <c>/api/messages</c>.</item>
///   <item>Host an ASP.NET Core endpoint that hands the request to a Bot Framework
///   <c>CloudAdapter</c>, which authenticates the JWT and produces an
///   <c>ITurnContext</c>.</item>
///   <item>An <c>IBot</c> implementation forwards each turn to
///   <see cref="ProcessActivityAsync"/> below. That method is the seam between the
///   webhook host and this adapter and is fully unit-testable: it depends only on
///   <see cref="ITurnContext"/>, which tests substitute.</item>
/// </list>
///
/// <see cref="StartAsync"/> arms the lifetime token and registers the handler so
/// the messaging endpoint can dispatch; <see cref="StopAsync"/> trips the token so
/// in-flight turns unwind. Per-turn work runs under try/catch with the lifetime
/// token, matching the Discord adapter's terminal-restart and shutdown patterns.
/// </summary>
public sealed class TeamsChatAdapter : IChatAdapter, IDisposable
{
    private readonly TeamsMessageTranslator _translator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TeamsChatAdapter> _logger;
    private readonly TeamsAdapterLifecycle _lifecycle = new();

    private Func<ChatMessage, IResponseSink, CancellationToken, Task>? _handler;

    public TeamsChatAdapter(
        IOptions<TeamsOptions> options,
        TeamsMessageTranslator translator,
        TimeProvider timeProvider,
        ILogger<TeamsChatAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(options.Value.AppId))
        {
            throw new ArgumentException(
                "TeamsOptions.AppId must be non-empty. Configure it via the " +
                "SCOUT__ADAPTERS__TEAMS__APPID environment variable or User Secrets — " +
                "never commit bot credentials.",
                nameof(options));
        }
    }

    public string Name => "Teams";

    /// <summary>
    /// Arm the adapter so the messaging endpoint can dispatch turns. Returns
    /// immediately: there is no socket to connect (see the class remarks).
    /// </summary>
    public Task StartAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Throws if already running; arms the lifetime token linked to the host.
        _lifecycle.Start(cancellationToken);
        _handler = handler;
        _logger.LogInformation(
            "Teams adapter armed; awaiting inbound activities on the messaging endpoint.");
        return Task.CompletedTask;
    }

    /// <summary>Trip the lifetime token so in-flight turns unwind. Re-startable.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await _lifecycle.StopAsync().ConfigureAwait(false))
        {
            return;
        }
        _handler = null;
        _logger.LogInformation("Teams adapter stopped.");
    }

    /// <summary>
    /// Entry point the webhook host's <c>IBot</c> calls for each inbound turn.
    /// Translates the activity, and when it is for Scout builds a sink bound to the
    /// ORIGINATING conversation (the descriptive-write boundary — a reply writes
    /// only where the message came from) and invokes the handler. The sink's send
    /// is bound to <paramref name="turnContext"/>, so streaming activities flow
    /// back into the same turn / conversation reference.
    /// </summary>
    public async Task ProcessActivityAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);

        var handler = _handler;
        // Read the running guard and lifetime token atomically: StopAsync can null
        // the CTS between the checks, so a separate IsRunning + LifetimeToken read
        // would race. A lost race is a clean shutdown no-op, not a processing error.
        if (handler is null || !_lifecycle.TryGetLifetimeToken(out var lifetimeToken))
        {
            return;
        }

        // Link the per-turn token to the lifetime token so host shutdown unwinds
        // an in-flight conversation.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetimeToken);
        var token = linked.Token;

        var activity = turnContext.Activity;
        var inbound = TeamsMessageTranslator.ProjectInbound(activity);

        try
        {
            if (!_translator.TryTranslate(inbound, out var chatMessage, out var reason))
            {
                _logger.LogDebug(
                    "Teams activity {ActivityId} ignored: {Reason}", inbound.ActivityId, reason);
                return;
            }

            var sink = BuildSink(turnContext, streamingSupported: !inbound.IsGroup);
            await handler(chatMessage!, sink, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Teams activity {ActivityId} cancelled on shutdown.", inbound.ActivityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "TeamsChatAdapter failed while processing activity {ActivityId}",
                inbound.ActivityId);
        }
    }

    // Build a sink whose send is bound to this turn's context. ITurnContext sends
    // back into the same conversation, honouring the descriptive-write boundary.
    // The SDK's ErrorResponseException is mapped to TeamsTransportException so the
    // sink's resilience reacts to status + Teams error code only.
    private TeamsResponseSink BuildSink(ITurnContext turnContext, bool streamingSupported)
    {
        var sender = new DelegateTeamsActivitySender(async (activity, ct) =>
        {
            try
            {
                var response = await turnContext.SendActivityAsync(activity, ct)
                    .ConfigureAwait(false);
                return response?.Id;
            }
            catch (Exception ex) when (TeamsErrorMapper.TryMap(ex, out var mapped))
            {
                throw mapped;
            }
        });

        return new TeamsResponseSink(
            sender,
            streamingSupported: streamingSupported,
            timeProvider: _timeProvider,
            logger: _logger);
    }

    public void Dispose() => _lifecycle.Dispose();
}
