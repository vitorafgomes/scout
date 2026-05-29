using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Adapter.Discord;
using Scout.Adapter.Slack;
using Scout.Adapter.Teams;
using Scout.Conversation;
using Scout.Host.Console;
using Scout.Llm;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddScoutLlm(builder.Configuration);
builder.Services.AddScoutConversation(builder.Configuration);

// Each adapter self-gates on its configured credentials, so the host runs
// whatever combination is configured and starts none when nothing is set.
builder.Services.AddDiscordAdapter(builder.Configuration);
builder.Services.AddSlackAdapter(builder.Configuration);
builder.Services.AddTeamsAdapter(builder.Configuration);

builder.Services.AddHostedService<ConversationWorker>();

var host = builder.Build();
await host.RunAsync();
