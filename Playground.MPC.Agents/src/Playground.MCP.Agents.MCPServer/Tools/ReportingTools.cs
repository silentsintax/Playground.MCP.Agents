 using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Playground.MCP.Agents.MCPServer.Tools
{
    [McpServerToolType]
    public sealed class ReportingTools(ILogger<ReportingTools> logger, IHttpClientFactory httpFactory)
    {
        [McpServerTool]
        [Description("Render a structured comparison result JSON into a formatted HTML report string. " +
                 "Returns the HTML string to be used in email body or saved to disk.")]
        public string RenderHtmlReport(
            [Description("The comparison result as a JSON string")] string resultJson,
            [Description("Optional heading override for the report")] string? heading = null)
        {
            logger.LogInformation("RenderHtmlReport called");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(resultJson); }
            catch { return "<p>Invalid JSON supplied to RenderHtmlReport.</p>"; }

            var root = doc.RootElement;
            var status = Str(root, "status") ?? "Unknown";
            var valuationDate = Str(root, "valuationDate") ?? "–";
            var sourceLabel = Str(root, "sourceLabel") ?? "Source";
            var targetLabel = Str(root, "targetLabel") ?? "Target";
            var sourceRows = Int(root, "totalSourceRows");
            var targetRows = Int(root, "totalTargetRows");
            var discCount = Int(root, "discrepancyCount");
            var agentAnalysis = Str(root, "agentAnalysis") ?? string.Empty;
            var runAt = Str(root, "runAt") ?? DateTime.UtcNow.ToString("u");

            var statusColor = status switch
            {
                "SuccessWithDiscrepancies" => "#d9534f",
                "Failed" => "#c0392b",
                _ => "#27ae60"
            };

            var sb = new StringBuilder();
            sb.AppendLine($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8"/>
              <style>
                body{font-family:Segoe UI,Arial,sans-serif;background:#f4f6f9;margin:0;padding:24px;}
                .card{background:#fff;border-radius:8px;padding:28px;max-width:820px;
                      margin:auto;box-shadow:0 2px 8px rgba(0,0,0,.08);}
                h1{font-size:1.4rem;margin-top:0;}
                .badge{display:inline-block;padding:4px 14px;border-radius:4px;
                       color:#fff;font-weight:700;background:{{statusColor}};}
                table{width:100%;border-collapse:collapse;margin:16px 0;}
                th{background:#f0f2f5;text-align:left;padding:8px 12px;font-size:.85rem;}
                td{padding:8px 12px;border-bottom:1px solid #eaecef;font-size:.9rem;}
                .disc-row td{background:#fff8f8;}
                .section{margin-top:24px;}
                .section h2{font-size:1rem;color:#555;border-bottom:2px solid #eaecef;
                             padding-bottom:6px;}
                pre{background:#f8f9fa;padding:14px;border-radius:6px;font-size:.82rem;
                    overflow-x:auto;white-space:pre-wrap;}
              </style>
            </head>
            <body>
            <div class="card">
              <h1>{{heading ?? "DailyValuations Comparison Report"}}</h1>
              <p>Run at: <strong>{{runAt}} UTC</strong> &nbsp;|&nbsp;
                 Valuation date: <strong>{{valuationDate}}</strong> &nbsp;|&nbsp;
                 Status: <span class="badge">{{status}}</span></p>

              <div class="section"><h2>Summary</h2>
              <table>
                <tr><th>Database</th><th>Rows</th></tr>
                <tr><td>{{sourceLabel}}</td><td>{{sourceRows:N0}}</td></tr>
                <tr><td>{{targetLabel}}</td><td>{{targetRows:N0}}</td></tr>
                <tr><th>Discrepancies</th><td><strong>{{discCount:N0}}</strong></td></tr>
              </table></div>
            """);

            if (root.TryGetProperty("discrepancies", out var disc) &&
                disc.ValueKind == JsonValueKind.Array && disc.GetArrayLength() > 0)
            {
                sb.AppendLine("""
                <div class="section"><h2>Discrepancies</h2>
                <table>
                  <tr><th>SecurityId</th><th>Date</th><th>Type</th><th>Fields</th></tr>
                """);

                foreach (var d in disc.EnumerateArray())
                {
                    var secId = d.TryGetProperty("securityId", out var s) ? s.GetInt32().ToString() : "–";
                    var dDate = d.TryGetProperty("valuationDate", out var dd) ? dd.GetString() : "–";
                    var type = d.TryGetProperty("type", out var t) ? t.GetString() : "–";
                    var fields = d.TryGetProperty("fieldDiffs", out var f) ? RenderFieldDiffs(f) : "–";
                    sb.AppendLine($"""
                    <tr class="disc-row">
                      <td>{secId}</td><td>{dDate}</td><td>{type}</td><td>{fields}</td>
                    </tr>
                    """);
                }
                sb.AppendLine("</table></div>");
            }

            if (!string.IsNullOrWhiteSpace(agentAnalysis))
            {
                var clean = agentAnalysis
                    .Replace("```json", string.Empty)
                    .Replace("```", string.Empty)
                    .Trim();
                sb.AppendLine($"""
                <div class="section"><h2>Agent Analysis</h2>
                <pre>{System.Net.WebUtility.HtmlEncode(clean)}</pre>
                </div>
                """);
            }

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }


        [McpServerTool]
        [Description("Send the comparison report by email using SMTP. " +
                "Accepts an HTML body string and a plain-text subject. " +
                "Returns 'OK' on success or an error message.")]
        public async Task<string> SendEmail(
           [Description("Email subject line")] string subject,
           [Description("HTML body of the email")] string htmlBody,
           [Description("Plain text fallback body")] string? textBody = null,CancellationToken ct = default)
        {
            logger.LogInformation("SendEmail: subject={Subject}", subject);

            var host = Env("SMTP_HOST");
            var port = int.Parse(Env("SMTP_PORT", "587"));
            var useSsl = bool.Parse(Env("SMTP_SSL", "true"));
            var user = Env("SMTP_USER");
            var pass = Env("SMTP_PASS");
            var from = Env("SMTP_FROM");
            var fromName = Env("SMTP_FROM_NAME", "Table Comparator");
            var toRaw = Env("SMTP_TO");
            var ccRaw = Env("SMTP_CC", "");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, from));

            foreach (var addr in toRaw.Split(';', StringSplitOptions.RemoveEmptyEntries))
                message.To.Add(MailboxAddress.Parse(addr.Trim()));

            foreach (var addr in ccRaw.Split(';', StringSplitOptions.RemoveEmptyEntries))
                message.Cc.Add(MailboxAddress.Parse(addr.Trim()));

            message.Subject = subject;

            var builder = new BodyBuilder
            {
                HtmlBody = htmlBody,
                TextBody = textBody ?? "Please open this email in an HTML-capable client."
            };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            var secureOption = useSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

            try
            {
                await smtp.ConnectAsync(host, port, secureOption, ct);
                if (!string.IsNullOrWhiteSpace(user))
                    await smtp.AuthenticateAsync(new NetworkCredential(user, pass), ct);

                await smtp.SendAsync(message, ct);
                await smtp.DisconnectAsync(true, ct);

                logger.LogInformation("Email sent to {To}", toRaw);
                return "OK";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SendEmail failed");
                return $"ERROR: {ex.Message}";
            }
        }


        [McpServerTool]
        [Description("Send a notification to a Microsoft Teams channel via an Incoming Webhook. " +
                 "Posts an Adaptive Card with the comparison summary. " +
                 "Returns 'OK' on success or an error message.")]
        public async Task<string> SendTeamsNotification(
            [Description("Short summary title")] string title,
            [Description("Status: OK | DISCREPANCIES_FOUND | FAILED")] string status,
            [Description("Valuation date yyyy-MM-dd")] string valuationDate,
            [Description("Number of source rows")] int sourceRows,
            [Description("Number of target rows")] int targetRows,
            [Description("Number of discrepancies")] int discrepancyCount,
            [Description("Key findings or recommendation text (1-3 sentences)")] string summary,CancellationToken ct = default)
        {
            logger.LogInformation("SendTeamsNotification: status={Status}", status);

            var webhookUrl = Env("TEAMS_WEBHOOK_URL");
            if (string.IsNullOrWhiteSpace(webhookUrl))
                return "ERROR: TEAMS_WEBHOOK_URL not set";

            var accentColor = status switch
            {
                "DISCREPANCIES_FOUND" => "attention",
                "FAILED" => "attention",
                _ => "good"
            };

            var statusEmoji = status switch
            {
                "DISCREPANCIES_FOUND" => "⚠️",
                "FAILED" => "❌",
                _ => "✅"
            };

            var card = new
            {
                type = "message",
                attachments = new[]
                {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type    = "AdaptiveCard",
                        version = "1.4",
                        body    = new object[]
                        {
                            new
                            {
                                type  = "TextBlock",
                                text  = $"{statusEmoji} {title}",
                                size  = "Large",
                                weight= "Bolder",
                                color = accentColor
                            },
                            new
                            {
                                type    = "FactSet",
                                facts   = new[]
                                {
                                    new { title = "Valuation Date",  value = valuationDate },
                                    new { title = "Status",          value = status },
                                    new { title = "Source Rows",     value = sourceRows.ToString("N0") },
                                    new { title = "Target Rows",     value = targetRows.ToString("N0") },
                                    new { title = "Discrepancies",   value = discrepancyCount.ToString("N0") }
                                }
                            },
                            new
                            {
                                type = "TextBlock",
                                text = summary,
                                wrap = true,
                                color = discrepancyCount > 0 ? "attention" : "default"
                            }
                        },
                        schema = "http://adaptivecards.io/schemas/adaptive-card.json"
                    }
                }
            }
            };

            var json = JsonSerializer.Serialize(card, new JsonSerializerOptions { WriteIndented = false });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var http = httpFactory.CreateClient("teams");
            try
            {
                var response = await http.PostAsync(webhookUrl, content, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Teams notification sent");
                    return "OK";
                }

                logger.LogWarning("Teams webhook returned {Status}: {Body}", response.StatusCode, body);
                return $"ERROR: HTTP {(int)response.StatusCode} – {body}";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SendTeamsNotification failed");
                return $"ERROR: {ex.Message}";
            }
        }


        private static string Env(string key, string? fallback = null) => Environment.GetEnvironmentVariable(key) 
            ?? fallback
            ?? throw new InvalidOperationException($"Required env var '{key}' not set");

        private static string? Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) ? v.GetString() : null;

        private static int Int(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;

        private static string RenderFieldDiffs(JsonElement fieldDiffs)
        {
            var parts = new List<string>();
            foreach (var prop in fieldDiffs.EnumerateObject())
            {
                var src = prop.Value.TryGetProperty("sourceValue", out var s) ? s.GetString() : "?";
                var tgt = prop.Value.TryGetProperty("targetValue", out var t) ? t.GetString() : "?";
                parts.Add($"{prop.Name}: {src} → {tgt}");
            }
            return string.Join("<br/>", parts);
        }
    }
}
