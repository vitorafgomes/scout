using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Adapter.Discord;
using Scout.Core.Hosting;
using Scout.Host.Worker;
using Scout.Llm.Ollama;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddScout(builder.Configuration)
    .AddOllamaLlmProvider()
    .AddDiscordAdapter(builder.Configuration)
    .AddHostedService<ScoutWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
