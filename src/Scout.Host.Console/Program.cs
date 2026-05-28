using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scout.Adapter.Discord;
using Scout.Conversation;
using Scout.Host.Console;
using Scout.Llm;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddScoutLlm(builder.Configuration);
builder.Services.AddScoutConversation(builder.Configuration);
builder.Services.AddDiscordAdapter(builder.Configuration);
builder.Services.AddHostedService<ConversationWorker>();

var host = builder.Build();
await host.RunAsync();
