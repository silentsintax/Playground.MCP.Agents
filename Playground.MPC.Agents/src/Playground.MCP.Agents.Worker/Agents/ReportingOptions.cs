namespace Playground.MCP.Agents.Worker.Agents
{
    public sealed class ReportingOptions
    {
        public const string Section = "Reporting";

        public EmailOptions Email { get; set; } = new();
        public TeamsOptions Teams { get; set; } = new();

        /// <summary>Only notify when discrepancies are found (skip clean runs).</summary>
        public bool NotifyOnlyOnDiscrepancies { get; set; } = false;
    }

    public sealed class EmailOptions
    {
        public bool Enabled { get; set; } = false;
        public string SmtpHost { get; set; } = "smtp.office365.com";
        public int SmtpPort { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string FromAddress { get; set; } = string.Empty;
        public string FromName { get; set; } = "Table Comparator";
        public List<string> ToAddresses { get; set; } = [];
        public List<string> CcAddresses { get; set; } = [];
    }

    public sealed class TeamsOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Incoming Webhook URL from Teams channel connector.
        /// Format: https://outlook.office.com/webhook/...
        /// Or Power Automate workflow URL.
        /// </summary>
        public string WebhookUrl { get; set; } = string.Empty;

        /// <summary>Only post to Teams when there are discrepancies.</summary>
        public bool OnlyOnDiscrepancies { get; set; } = false;
    }
}
