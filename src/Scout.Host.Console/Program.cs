using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Host.Console;

var command = ConsoleCommand.Parse(args);

if (command.Command == Command.Index)
{
    // One-shot index: build the minimal graph only (LLM embeddings + project
    // knowledge), with the same explicit content root so config loads from the
    // binary directory regardless of the working directory. No adapters, no
    // conversation, no skills, no host loop.
    var indexBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    indexBuilder.Logging.ClearProviders();
    indexBuilder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    indexBuilder.Services.AddScoutIndexHost(indexBuilder.Configuration);

    using var indexHost = indexBuilder.Build();
    return await IndexEntryPoint.RunAsync(indexHost.Services, command.Project, CancellationToken.None);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Keep stdout clean: replace the default console logger with one that writes
// everything to stderr, so the SCOUT_READY marker is the only thing the
// supervisor ever parses on stdout.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddScoutHost(builder.Configuration);

// Startup visibility (synchronous, never buffered). The async console logger can
// drop queued messages on a fast startup/shutdown, hiding why the host stalled or
// exited. These markers bypass the logging pipeline and always reach stderr.
var configuredAdapters = new List<string>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Scout:Adapters:Discord:BotToken"]))
{
    configuredAdapters.Add("Discord");
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Scout:Adapters:Slack:BotToken"]))
{
    configuredAdapters.Add("Slack");
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Scout:Adapters:Teams:AppId"]))
{
    configuredAdapters.Add("Teams");
}

WriteMarker($"env={builder.Environment.EnvironmentName} adapters configured: " +
    (configuredAdapters.Count == 0 ? "none" : string.Join(", ", configuredAdapters)));

var host = builder.Build();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => WriteMarker("host started"));
lifetime.ApplicationStopping.Register(() => WriteMarker("host stopping"));

WriteMarker("running host");
await host.RunAsync();
return 0;

static void WriteMarker(string message)
{
    System.Console.Error.WriteLine($"[scout] {message}");
    System.Console.Error.Flush();
}
