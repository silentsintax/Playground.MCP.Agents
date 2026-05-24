using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Playground.MCP.Agents.Code.Models;
using Playground.MCP.Agents.Worker.Agents;
using System.Diagnostics;

namespace Playground.MCP.Agents.Worker.Skills
{
    public sealed class FixedIncomeDailyValuationCompareSkill(
        IOptions<AgentOptions> agentOpts,
        IOptions<ConnectionOptions> connOpts,
        IHttpClientFactory httpFactory,
        ILogger<FixedIncomeDailyValuationCompareSkill> logger)
    {
        public async Task<ComparisonResult> ExecuteAsync(CancellationToken ct = default)
        {
            logger.LogInformation("Skill: starting MCP server process…");

            var mcpExe = ResolveMcpServerPath();

            var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Playground.MCP.Agents.MCPServer.DailyValuationComparator",
                Command = mcpExe,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["SOURCE_CONN"] = connOpts.Value.SourceConnectionString,
                    ["TARGET_CONN"] = connOpts.Value.TargetConnectionString
                }
            });

            await using var mcpClient = await McpClient.CreateAsync(clientTransport, null, null, ct);

            logger.LogInformation("Skill: MCP client connected");

            var httpClient = httpFactory.CreateClient("ollama");

            var agent = new DailyValuationsComparerAgent(mcpClient, httpClient, agentOpts, logger.CreateLogger<DailyValuationsComparerAgent>());

            var sw = Stopwatch.StartNew();
            var result = await agent.RunAsync(ct);
            sw.Stop();

            logger.LogInformation(
                "Skill: completed in {Elapsed}ms — status={Status} discrepancies={Count}",
                sw.ElapsedMilliseconds, result.Status, result.DiscrepancyCount);

            return result;
        }

        private static string ResolveMcpServerPath()
        {
            var dir = AppContext.BaseDirectory;
            var execName = OperatingSystem.IsWindows()
                ? "Playground.MCP.Agents.MCPServer.exe"
                : "Playground.MCP.Agents.MCPServer";

            var candidate = Path.Combine(dir, execName);
            if (File.Exists(candidate)) return candidate;

            var solutionRoot = FindSolutionRoot(dir);
            if (solutionRoot is not null)
            {
                var devPath = Path.Combine(solutionRoot,
                    "src", "Playground.MCP.Agents.MCPServer", "bin", "Debug", "net10.0", execName);
                if (File.Exists(devPath)) return devPath;
            }

            throw new FileNotFoundException(
                $"MCP server binary '{execName}' not found. Build all projects first.", execName);
        }

        private static string? FindSolutionRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }

    //Typed Logger
    file static class LoggerExtensions
    {
        public static ILogger<T> CreateLogger<T>(this ILogger logger) =>
            logger is ILoggerFactory f
                ? f.CreateLogger<T>()
                : new WrappedLogger<T>(logger);
    }

    file sealed class WrappedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel l) => inner.IsEnabled(l);
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
            => inner.Log(l, e, s, ex, f);
    }
}
