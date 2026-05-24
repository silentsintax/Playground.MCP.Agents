namespace Playground.MCP.Agents.Code.Models
{
    public record ComparisonResult
    {
        public Guid RunId { get; init; } = Guid.NewGuid();
        public DateTime RunAt { get; init; } = DateTime.UtcNow;
        public string SourceLabel { get; init; } = string.Empty;
        public string TargetLabel { get; init; } = string.Empty;
        public DateOnly? ValuationDate { get; init; }
        public int TotalSourceRows { get; init; }
        public int TotalTargetRows { get; init; }
        public int MatchingRows { get; init; }
        public int DiscrepancyCount { get; init; }
        public List<TableDiscrepancy> Discrepancies { get; init; } = [];
        public string? AgentAnalysis { get; init; }
        public ComparisonStatus Status { get; init; }
        public string? ErrorMessage { get; init; }

        public bool HasDiscrepancies => DiscrepancyCount > 0;
    }

    public enum ComparisonStatus
    {
        Success,
        SuccessWithDiscrepancies,
        Failed
    }

    public record TableDiscrepancy
    {
        public int SecurityId { get; init; }
        public DateOnly ValuationDate { get; init; }
        public DiscrepancyType Type { get; init; }
        public Dictionary<string, FieldDiff> FieldDiffs { get; init; } = [];
    }

    public enum DiscrepancyType
    {
        MissingInTarget,
        MissingInSource,
        ValueMismatch
    }

    public record FieldDiff(string? SourceValue, string? TargetValue)
    {
        public decimal? Delta =>
            decimal.TryParse(SourceValue, out var s) && decimal.TryParse(TargetValue, out var t)
                ? Math.Abs(s - t)
                : null;
    }

    public record TableStats
    {
        public int RowCount { get; init; }
        public decimal? TotalMarketValue { get; init; }
        public decimal? TotalAccrualValue { get; init; }
        public decimal? TotalUnrealizedPnL { get; init; }
        public DateOnly? MinDate { get; init; }
        public DateOnly? MaxDate { get; init; }
        public int DistinctSecurities { get; init; }
    }
}
