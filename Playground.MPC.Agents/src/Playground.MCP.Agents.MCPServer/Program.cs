using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Playground.MCP.Agents.MCPServer;
using Playground.MCP.Agents.MCPServer.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<FixedIncomeDailyValuationCompareTool>();

builder.Services.AddSingleton<SqlConnectionFactory>();

var app = builder.Build();
await app.RunAsync();

