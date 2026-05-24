using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Playground.MCP.Agents.Code.Models;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Playground.MCP.Agents.MCPServer.Tools
{
    /// <summary>
    /// Responsavel por comparar as avaliações diárias de títulos de renda fixa, identificando discrepâncias e fornecendo insights para otimização de portfólio.
    /// </summary>
    [McpServerToolType]
    public sealed class FixedIncomeDailyValuationCompareTool(SqlConnectionFactory factory, ILogger<FixedIncomeDailyValuationCompareTool> logger)
    {
        [McpServerTool]
        [Description("Get summary statistics for DailyValuations in source or target database. " +
                 "Optionally filter by a specific valuation date (format: yyyy-MM-dd).")]
        public async Task<string> GetTableStats(
            [Description("Which database: 'source' or 'target'")] string database,
            [Description("Optional date filter yyyy-MM-dd")] string? valuationDate = null,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation("GetTableStats: db={Db} date={Date}", database, valuationDate);

            await using var conn = database == "source" ? factory.OpenSource() : factory.OpenTarget();

            var dateFilter = valuationDate is not null
                ? "WHERE ValuationDate = @date"
                : string.Empty;

            var sql = $"""
            SELECT
                COUNT(*)                  AS RowCount,
                SUM(MarketValue)          AS TotalMarketValue,
                SUM(AccrualValue)         AS TotalAccrualValue,
                SUM(UnrealizedPnL)        AS TotalUnrealizedPnL,
                MIN(ValuationDate)        AS MinDate,
                MAX(ValuationDate)        AS MaxDate,
                COUNT(DISTINCT SecurityId) AS DistinctSecurities
            FROM DailyValuations
            {dateFilter}
            """;

            await using var cmd = new SqlCommand(sql, conn);
            if (valuationDate is not null)
                cmd.Parameters.AddWithValue("@date", valuationDate);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
                return "No rows found";

            var stats = new TableStats
            {
                RowCount = reader.GetInt32(0),
                TotalMarketValue = reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                TotalAccrualValue = reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                TotalUnrealizedPnL = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                MinDate = reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
                MaxDate = reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
                DistinctSecurities = reader.GetInt32(6)
            };

            return JsonSerializer.Serialize(stats, JsonOptions);
        }


        [McpServerTool]
        [Description("Compare DailyValuations between source and target databases for a specific date. " +
                 "Returns a list of discrepancies (missing rows and value mismatches). " +
                 "tolerance is the max absolute difference considered acceptable (default 0.01).")]
        public async Task<string> CompareValorizationTables(
            [Description("Valuation date to compare, format yyyy-MM-dd")] string valuationDate,
            [Description("Numeric tolerance for decimal fields (default 0.01)")] double tolerance = 0.01,
            [Description("Max discrepancies to return (default 50)")] int maxRows = 50,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation("CompareTables: date={Date} tol={Tol}", valuationDate, tolerance);

            var tol = (decimal)tolerance;
            var discrepancies = new List<TableDiscrepancy>();

            // Carrega as linhas de valorizacao das tabelas
            var sourceRows = await LoadRowsAsync(factory.OpenSource(), valuationDate, cancellationToken);
            var targetRows = await LoadRowsAsync(factory.OpenTarget(), valuationDate, cancellationToken);

            var sourceIndex = sourceRows.ToDictionary(r => r.SecurityId);
            var targetIndex = targetRows.ToDictionary(r => r.SecurityId);

            // Linhas faltantes na tabela de destino
            foreach (var (secId, src) in sourceIndex)
            {
                if (discrepancies.Count >= maxRows) break;
                if (!targetIndex.ContainsKey(secId))
                    discrepancies.Add(new TableDiscrepancy
                    {
                        SecurityId = secId,
                        ValuationDate = src.ValuationDate,
                        Type = DiscrepancyType.MissingInTarget
                    });
            }

            // // Linhas faltantes na tabela de origem
            foreach (var (secId, tgt) in targetIndex)
            {
                if (discrepancies.Count >= maxRows) break;
                if (!sourceIndex.ContainsKey(secId))
                    discrepancies.Add(new TableDiscrepancy
                    {
                        SecurityId = secId,
                        ValuationDate = tgt.ValuationDate,
                        Type = DiscrepancyType.MissingInSource
                    });
            }

            // Valores divergentes
            foreach (var (secId, src) in sourceIndex)
            {
                if (discrepancies.Count >= maxRows) break;
                if (!targetIndex.TryGetValue(secId, out var tgt)) continue;

                var diffs = BuildDiffs(src, tgt, tol);
                if (diffs.Count == 0) continue;

                discrepancies.Add(new TableDiscrepancy
                {
                    SecurityId = secId,
                    ValuationDate = src.ValuationDate,
                    Type = DiscrepancyType.ValueMismatch,
                    FieldDiffs = diffs
                });
            }

            var summary = new
            {
                ValuationDate = valuationDate,
                SourceRowCount = sourceRows.Count,
                TargetRowCount = targetRows.Count,
                Tolerance = tolerance,
                DiscrepancyCount = discrepancies.Count,
                Discrepancies = discrepancies
            };

            return JsonSerializer.Serialize(summary, JsonOptions);
        }

        [McpServerTool]
        [Description("Get detailed rows for a specific SecurityId across both source and target, " +
                 "optionally filtered by date range. Useful for deep-diving a discrepancy.")]
        public async Task<string> GetSecurityDetail(
            [Description("The SecurityId to inspect")] int securityId,
            [Description("Start date yyyy-MM-dd (optional)")] string? fromDate = null,
            [Description("End date yyyy-MM-dd (optional)")] string? toDate = null,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation("GetSecurityDetail: secId={SecId}", securityId);

            var srcRows = await LoadSecurityRowsAsync(factory.OpenSource(), securityId, fromDate, toDate, cancellationToken);
            var tgtRows = await LoadSecurityRowsAsync(factory.OpenTarget(), securityId, fromDate, toDate, cancellationToken);

            return JsonSerializer.Serialize(new { SourceRows = srcRows, TargetRows = tgtRows }, JsonOptions);
        }

        [McpServerTool]
        [Description("Get the last N distinct valuation dates available in source and target databases, " +
                 "so the agent can decide which dates to compare. Default N=5.")]
        public async Task<string> GetAvailableDates(
            [Description("How many recent dates to return")] int lastN = 5,
            CancellationToken cancellationToken = default)
        {
            var sourceDates = await FetchDatesAsync(factory.OpenSource(), lastN, cancellationToken);
            var targetDates = await FetchDatesAsync(factory.OpenTarget(), lastN, cancellationToken);

            var result = new
            {
                SourceDates = sourceDates,
                TargetDates = targetDates,
                CommonDates = sourceDates.Intersect(targetDates).OrderByDescending(d => d).ToList(),
                OnlyInSource = sourceDates.Except(targetDates).ToList(),
                OnlyInTarget = targetDates.Except(sourceDates).ToList()
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }

        private static async Task<List<ValuationRow>> LoadRowsAsync(SqlConnection conn, string valuationDate, CancellationToken ct)
        {
            await using var _ = conn;
            const string sql = """
            SELECT SecurityId, ValuationDate,
                   AccrualPU, AccrualValue, MarketPU, MarketValue,
                   UnrealizedPnL, DailyPnL, MtMImpact,
                   IndexerRate, DailyFactor
            FROM DailyValuations
            WHERE ValuationDate = @date
            ORDER BY SecurityId
            """;

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@date", valuationDate);

            var rows = new List<ValuationRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(ValuationRow.FromReader(reader));

            return rows;
        }

        private static async Task<List<string>> FetchDatesAsync(
            SqlConnection conn, int n, CancellationToken ct)
        {
            await using var _ = conn;
            var sql = $"SELECT TOP {n} CONVERT(varchar,ValuationDate,23) FROM DailyValuations " +
                      "GROUP BY ValuationDate ORDER BY ValuationDate DESC";

            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var dates = new List<string>();
            while (await reader.ReadAsync(ct))
                dates.Add(reader.GetString(0));

            return dates;
        }

        private static async Task<List<object>> LoadSecurityRowsAsync(
            SqlConnection conn, int securityId, string? from, string? to, CancellationToken ct)
        {
            await using var _ = conn;
            var sb = new StringBuilder("""
            SELECT SecurityId, ValuationDate,
                   AccrualPU, AccrualValue, MarketPU, MarketValue,
                   UnrealizedPnL, DailyPnL, MtMImpact, IndexerRate, DailyFactor
            FROM DailyValuations
            WHERE SecurityId = @secId
            """);
            if (from is not null) sb.Append(" AND ValuationDate >= @from");
            if (to is not null) sb.Append(" AND ValuationDate <= @to");
            sb.Append(" ORDER BY ValuationDate DESC");

            await using var cmd = new SqlCommand(sb.ToString(), conn);
            cmd.Parameters.AddWithValue("@secId", securityId);
            if (from is not null) cmd.Parameters.AddWithValue("@from", from);
            if (to is not null) cmd.Parameters.AddWithValue("@to", to);

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(ValuationRow.FromReader(reader));

            return rows;
        }

        private static Dictionary<string, FieldDiff> BuildDiffs(
            ValuationRow src, ValuationRow tgt, decimal tol)
        {
            var diffs = new Dictionary<string, FieldDiff>();

            void Check(string name, decimal s, decimal t)
            {
                if (Math.Abs(s - t) > tol)
                    diffs[name] = new FieldDiff(s.ToString("F6"), t.ToString("F6"));
            }

            Check(nameof(ValuationRow.AccrualPU), src.AccrualPU, tgt.AccrualPU);
            Check(nameof(ValuationRow.AccrualValue), src.AccrualValue, tgt.AccrualValue);
            Check(nameof(ValuationRow.MarketPU), src.MarketPU, tgt.MarketPU);
            Check(nameof(ValuationRow.MarketValue), src.MarketValue, tgt.MarketValue);
            Check(nameof(ValuationRow.UnrealizedPnL), src.UnrealizedPnL, tgt.UnrealizedPnL);
            Check(nameof(ValuationRow.DailyPnL), src.DailyPnL, tgt.DailyPnL);
            Check(nameof(ValuationRow.MtMImpact), src.MtMImpact, tgt.MtMImpact);
            Check(nameof(ValuationRow.IndexerRate), src.IndexerRate, tgt.IndexerRate);
            Check(nameof(ValuationRow.DailyFactor), src.DailyFactor, tgt.DailyFactor);

            return diffs;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        internal sealed record ValuationRow
        {
            public int SecurityId { get; init; }
            public DateOnly ValuationDate { get; init; }
            public decimal AccrualPU { get; init; }
            public decimal AccrualValue { get; init; }
            public decimal MarketPU { get; init; }
            public decimal MarketValue { get; init; }
            public decimal UnrealizedPnL { get; init; }
            public decimal DailyPnL { get; init; }
            public decimal MtMImpact { get; init; }
            public decimal IndexerRate { get; init; }
            public decimal DailyFactor { get; init; }

            public static ValuationRow FromReader(SqlDataReader r) => new()
            {
                SecurityId = r.GetInt32(0),
                ValuationDate = DateOnly.FromDateTime(r.GetDateTime(1)),
                AccrualPU = r.GetDecimal(2),
                AccrualValue = r.GetDecimal(3),
                MarketPU = r.GetDecimal(4),
                MarketValue = r.GetDecimal(5),
                UnrealizedPnL = r.GetDecimal(6),
                DailyPnL = r.GetDecimal(7),
                MtMImpact = r.GetDecimal(8),
                IndexerRate = r.GetDecimal(9),
                DailyFactor = r.GetDecimal(10)
            };
        }
    }
}
