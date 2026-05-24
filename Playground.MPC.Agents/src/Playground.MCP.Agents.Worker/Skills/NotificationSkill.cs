using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Playground.MCP.Agents.Code.Models;
using Playground.MCP.Agents.Worker.Agents;
using System.Diagnostics;

namespace Playground.MCP.Agents.Worker.Skills
{
    public sealed class NotificationSkill(IOptions<AgentOptions> agentOpts,
        IOptions<ReportingOptions> reportingOpts,
        IOptions<ConnectionOptions> connOpts,
        IHttpClientFactory httpFactory,
        ILogger<NotificationSkill> logger)
    {
        public async Task ExecuteAsync(ComparisonResult result, CancellationToken ct = default)
        {
            var reporting = reportingOpts.Value;

            if (!reporting.Email.Enabled && !reporting.Teams.Enabled)
            {
                logger.LogDebug("NotificationSkill: both email and Teams disabled — skipping");
                return;
            }

            if (reporting.NotifyOnlyOnDiscrepancies && !result.HasDiscrepancies)
            {
                logger.LogInformation("NotificationSkill: run clean, no notification needed");
                return;
            }

            logger.LogInformation("NotificationSkill: dispatching report for run {RunId}", result.RunId);

            var mcpExe = ResolveMcpServerPath();

            var envVars = BuildEnvVars(connOpts.Value, reporting);

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Playground.MCP.Agents.MCPServer.Reporting",
                Command = mcpExe,
                EnvironmentVariables = envVars
            });

            await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: ct);

            var httpClient = httpFactory.CreateClient("ollama");

            var agent = new ReportingAgent(
                mcpClient, httpClient, agentOpts, reportingOpts,
                logger.CreateLogger<ReportingAgent>());

            var sw = Stopwatch.StartNew();
            await agent.RunAsync(result, ct);
            sw.Stop();

            logger.LogInformation("NotificationSkill: completed in {Elapsed}ms", sw.ElapsedMilliseconds);
        }

        private static Dictionary<string, string> BuildEnvVars(ConnectionOptions conn, ReportingOptions r)
        {
            var env = new Dictionary<string, string>
            {
                // DB connections (needed even for the reporting-only session)
                ["SOURCE_CONN"] = conn.SourceConnectionString,
                ["TARGET_CONN"] = conn.TargetConnectionString
            };

            // SMTP
            if (r.Email.Enabled)
            {
                env["SMTP_HOST"] = r.Email.SmtpHost;
                env["SMTP_PORT"] = r.Email.SmtpPort.ToString();
                env["SMTP_SSL"] = r.Email.UseSsl.ToString();
                env["SMTP_USER"] = r.Email.Username;
                env["SMTP_PASS"] = r.Email.Password;
                env["SMTP_FROM"] = r.Email.FromAddress;
                env["SMTP_FROM_NAME"] = r.Email.FromName;
                env["SMTP_TO"] = string.Join(";", r.Email.ToAddresses);
                env["SMTP_CC"] = string.Join(";", r.Email.CcAddresses);
            }

            // Teams
            if (r.Teams.Enabled)
                env["TEAMS_WEBHOOK_URL"] = r.Teams.WebhookUrl;

            return env;
        }

        private static string ResolveMcpServerPath()
        {
            var execName = OperatingSystem.IsWindows()
                ? "Playground.MCP.Agents.MCPServer.exe"
                : "Playground.MCP.Agents.MCPServer";

            var candidate = Path.Combine(AppContext.BaseDirectory, execName);
            if (File.Exists(candidate)) return candidate;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (dir.GetFiles("*.sln").Length > 0)
                {
                    var devPath = Path.Combine(dir.FullName,
                        "src", "Playground.MCP.Agents.MCPServer", "bin", "Debug", "net10.0", execName);
                    if (File.Exists(devPath)) return devPath;
                    break;
                }
                dir = dir.Parent;
            }

            throw new FileNotFoundException($"MCP server binary '{execName}' not found.", execName);
        }
    }

    file static class LoggerExtensions
    {
        public static ILogger<T> CreateLogger<T>(this ILogger logger) =>
            logger is ILoggerFactory f ? f.CreateLogger<T>() : new WrappedLogger<T>(logger);
    }

    file sealed class WrappedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel l) => inner.IsEnabled(l);
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
            => inner.Log(l, e, s, ex, f);
    }
}
