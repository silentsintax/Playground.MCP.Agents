using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Playground.MCP.Agents.Code.Models;
using System.Text.Json;

namespace Playground.MCP.Agents.Worker.Skills
{
    public sealed class ReportGenerationSkill(IOptions<ReportOptions> opts, ILogger<ReportGenerationSkill> logger)
    {
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        public async Task ExecuteAsync(ComparisonResult result, CancellationToken ct = default)
        {
            LogSummary(result);

            if (opts.Value.WriteJsonReports)
                await WriteJsonReportAsync(result, ct);
        }

        private void LogSummary(ComparisonResult r)
        {
            var level = r.Status == ComparisonStatus.SuccessWithDiscrepancies
                ? LogLevel.Warning : LogLevel.Information;

            logger.Log(level,
                "Comparison {RunId}: {Status} | Date={Date} | " +
                "Source={SourceRows} rows | Target={TargetRows} rows | Discrepancies={Disc}",
                r.RunId, r.Status, r.ValuationDate,
                r.TotalSourceRows, r.TotalTargetRows, r.DiscrepancyCount);

            if (r.AgentAnalysis is not null)
            {
                var preview = r.AgentAnalysis.Length > 500
                    ? r.AgentAnalysis[..500] + " [truncated]"
                    : r.AgentAnalysis;

                logger.LogInformation("Agent analysis:\n{Analysis}", preview);
            }

            foreach (var d in r.Discrepancies.Take(5))
            {
                logger.LogWarning(
                    "  Discrepancy: SecurityId={SecId} Type={Type} Fields={Fields}",
                    d.SecurityId, d.Type,
                    string.Join(", ", d.FieldDiffs.Select(kv => $"{kv.Key}(Δ={kv.Value.Delta:F4})")));
            }
        }

        private async Task WriteJsonReportAsync(ComparisonResult result, CancellationToken ct)
        {
            var dir = opts.Value.ReportDirectory ?? Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(dir);

            var fileName = $"comparison_{result.RunAt:yyyyMMdd_HHmmss}_{result.RunId:N[..8]}.json";
            var path = Path.Combine(dir, fileName);

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, result, Pretty, ct);

            logger.LogInformation("Report written to {Path}", path);
        }
    }

    public sealed class ReportOptions
    {
        public const string Section = "Report";
        public bool WriteJsonReports { get; set; } = true;
        public string? ReportDirectory { get; set; }
    }
}
