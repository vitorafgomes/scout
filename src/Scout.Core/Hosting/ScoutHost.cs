using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Core.Chat;
using Scout.Core.Llm;
using Scout.Core.Plugins;
using Scout.Core.Telemetry;

namespace Scout.Core.Hosting;

/// <summary>
/// The single entry point chat adapters call for every inbound message.
/// Owns the composed Semantic Kernel (built lazily from the plugin registry
/// and the selected LLM provider factory) and emits tracing for each call.
///
/// V1 behavior is deliberately minimal — a static handshake that reports how
/// many plugins are loaded — so the host is observably alive end-to-end
/// before intent routing or plugin invocation lands.
/// </summary>
public sealed class ScoutHost
{
    private readonly PluginRegistry _registry;
    private readonly ILlmProviderFactory _llmFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<ScoutHost> _logger;
    private readonly ScoutOptions _options;
    private readonly Lazy<Kernel> _kernel;

    public ScoutHost(
        PluginRegistry registry,
        ILlmProviderFactory llmFactory,
        IServiceProvider services,
        ILogger<ScoutHost> logger,
        IOptions<ScoutOptions> options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));

        _kernel = new Lazy<Kernel>(BuildKernel);
    }

    public async Task<ChatResponse> HandleAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var activity = ScoutActivitySource.Source.StartActivity("scout.handle");
        activity?.SetTag("scout.channel", message.ChannelId);
        activity?.SetTag("scout.user", message.UserId);
        activity?.SetTag("scout.adapter", _options.ChatAdapter);

        try
        {
            await Task.Yield();

            var pluginCount = _registry.Plugins.Count;
            var text =
                $"Scout is online. {pluginCount} plugin(s) loaded. " +
                $"Ask me something specific.";

            return ChatResponse.Plain(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scout failed to handle message from {User}", message.UserId);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            return ChatResponse.Plain(
                "Sorry, something went wrong while answering. Please try again.");
        }
    }

    private Kernel BuildKernel()
    {
        var builder = Kernel.CreateBuilder();
        _llmFactory.AddToKernel(builder);
        var kernel = builder.Build();

        foreach (var plugin in _registry.BuildAll(_services))
        {
            kernel.Plugins.Add(plugin);
        }

        return kernel;
    }
}
