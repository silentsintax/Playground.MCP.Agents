using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCrontab;
using Playground.MCP.Agents.Worker.Skills;

namespace Playground.MCP.Agents.Worker
{
    public sealed class Worker(
        FixedIncomeDailyValuationCompareSkill comparisonSkill,
        ReportGenerationSkill reportSkill,
        NotificationSkill notificationSkill,
        IOptions<ScheduleOptions> scheduleOpts,
        ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var cron = CrontabSchedule.Parse(
                scheduleOpts.Value.CronExpression,
                new CrontabSchedule.ParseOptions { IncludingSeconds = false });

            logger.LogInformation(
                "ComparisonWorker started. Cron={Cron}. Next run at {Next}",
                scheduleOpts.Value.CronExpression,
                cron.GetNextOccurrence(DateTime.Now));

            if (scheduleOpts.Value.RunOnStartup)
                await RunComparisonAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var next = cron.GetNextOccurrence(now);
                var wait = next - now;

                logger.LogInformation("Next comparison scheduled at {Next} (in {Wait:mm\\:ss})", next, wait);

                try
                {
                    await Task.Delay(wait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // Host is shutting down
                }

                if (!stoppingToken.IsCancellationRequested)
                    await RunComparisonAsync(stoppingToken);
            }

            logger.LogInformation("ComparisonWorker stopped.");
        }

        private async Task RunComparisonAsync(CancellationToken ct)
        {
            logger.LogInformation("════════════════ Starting comparison run ════════════════");
            try
            {
                // Skill 1: agent-driven comparison (OllamaAgent + MCP SQL tools)
                var result = await comparisonSkill.ExecuteAsync(ct);

                // Skill 2: persist JSON report to disk
                await reportSkill.ExecuteAsync(result, ct);

                // Skill 3: reporting agent → email + Teams via MCP reporting tools
                await notificationSkill.ExecuteAsync(result, ct);

                logger.LogInformation("════════════════ Run complete: {Status} ════════════════", result.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Comparison run failed");
            }
        }
    }

    public sealed class ScheduleOptions
    {
        public const string Section = "Schedule";

        /// <summary>Standard 5-field cron expression (no seconds).</summary>
        public string CronExpression { get; set; } = "*/30 * * * *";   // every 30 min

        /// <summary>If true, one run executes immediately at startup before the first scheduled run.</summary>
        public bool RunOnStartup { get; set; } = true;
    }
}
