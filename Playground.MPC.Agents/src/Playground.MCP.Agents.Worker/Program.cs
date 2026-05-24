using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Playground.MCP.Agents.Worker;
using Playground.MCP.Agents.Worker.Agents;
using Playground.MCP.Agents.Worker.Skills;

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration sections ────────────────────────────────────────────────────
builder.Services
    .Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section))
    .Configure<ConnectionOptions>(builder.Configuration.GetSection(ConnectionOptions.Section))
    .Configure<ReportOptions>(builder.Configuration.GetSection(ReportOptions.Section))
    .Configure<ReportingOptions>(builder.Configuration.GetSection(ReportingOptions.Section))
    .Configure<ScheduleOptions>(builder.Configuration.GetSection(ScheduleOptions.Section));

// ── Ollama HTTP client ────────────────────────────────────────────────────────
var ollamaUrl = builder.Configuration
    .GetSection(AgentOptions.Section)
    .GetValue<string>("OllamaBaseUrl") ?? "http://localhost:11434";

builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(ollamaUrl);
    client.Timeout = TimeSpan.FromMinutes(10);  // LLM reasoning can take a while
});

// Generic HttpClient for Teams webhook (no special base address)
builder.Services.AddHttpClient("teams");

// ── Skills ────────────────────────────────────────────────────────────────────
builder.Services.AddTransient<FixedIncomeDailyValuationCompareSkill>();
builder.Services.AddTransient<ReportGenerationSkill>();
builder.Services.AddTransient<NotificationSkill>();    

// ── Background worker ─────────────────────────────────────────────────────────
builder.Services.AddHostedService<Worker>();

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.AddConsole();

var host = builder.Build();
await host.RunAsync();
