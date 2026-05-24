using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Playground.MCP.Agents.Code.Models;
using System.Text;
using System.Text.Json;

namespace Playground.MCP.Agents.Worker.Agents
{
    public sealed class ReportingAgent(
        McpClient mcpClient,
        HttpClient httpClient,
        IOptions<AgentOptions> agentOpts,
        IOptions<ReportingOptions> reportingOpts,
        ILogger<ReportingAgent> logger)
    {
        private readonly AgentOptions _agent = agentOpts.Value;
        private readonly ReportingOptions _reporting = reportingOpts.Value;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public async Task RunAsync(ComparisonResult result, CancellationToken ct = default)
        {
            if (_reporting.NotifyOnlyOnDiscrepancies && !result.HasDiscrepancies)
            {
                logger.LogInformation("ReportingAgent: no discrepancies, skipping notification");
                return;
            }

            if (!_reporting.Email.Enabled && !_reporting.Teams.Enabled)
            {
                logger.LogInformation("ReportingAgent: email and Teams both disabled, nothing to do");
                return;
            }

            logger.LogInformation("ReportingAgent: building and dispatching report for run {RunId}", result.RunId);

            // 1. Discover reporting tools exposed by MCP server
            var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: ct);
            var ollamaTools = mcpTools
                .Where(t => t.Name is "RenderHtmlReport" or "SendEmail" or "SendTeamsNotification")
                .Select(t => ToOllamaTool(t))
                .ToList();

            logger.LogInformation("ReportingAgent: {Count} reporting tools available", ollamaTools.Count);

            // 2. Serialize the ComparisonResult to give to the agent
            var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

            // 3. Build the conversation
            var messages = new List<OllamaMessage>
        {
            SystemMessage(result),
            UserMessage(resultJson)
        };

            // 4. ReAct loop
            const int maxIterations = 10;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                logger.LogInformation("ReportingAgent: iteration {Iter}", iter + 1);

                var response = await CallOllamaAsync(messages, ollamaTools, ct);
                messages.Add(response);

                if (response.ToolCalls is not { Count: > 0 })
                {
                    logger.LogInformation("ReportingAgent: done — {Msg}",
                        response.Content?.Length > 120
                            ? response.Content[..120] + "…"
                            : response.Content);
                    break;
                }

                foreach (var toolCall in response.ToolCalls)
                {
                    logger.LogInformation("ReportingAgent: → {Tool}", toolCall.Function.Name);

                    var toolResult = await InvokeMcpToolAsync(toolCall, ct);

                    logger.LogInformation("ReportingAgent: ← {Tool}: {Result}",
                        toolCall.Function.Name,
                        toolResult.Length > 80 ? toolResult[..80] + "…" : toolResult);

                    messages.Add(new OllamaMessage
                    {
                        Role = "tool",
                        Content = toolResult,
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Function.Name
                    });
                }
            }
        }

        private OllamaMessage SystemMessage(ComparisonResult result) => new()
        {
            Role = "system",
            Content = $"""
            You are a reporting agent for a financial data quality system.
            You have access to three tools: RenderHtmlReport, SendEmail, SendTeamsNotification.

            ALWAYS follow this sequence:
            1. Call RenderHtmlReport with the result JSON to get an HTML string.
            2. If Teams is enabled ({_reporting.Teams.Enabled}):
               Call SendTeamsNotification with:
               - A concise title (max 10 words)
               - status, valuationDate, sourceRows, targetRows, discrepancyCount from the result
               - A 1-2 sentence summary highlighting the most important finding
            3. If Email is enabled ({_reporting.Email.Enabled}):
               Call SendEmail with:
               - subject: a professional subject line mentioning the date and status
               - htmlBody: the HTML string from step 1
               - textBody: a plain-text version of the summary
            4. When all configured channels have been notified, reply with a short confirmation.

            IMPORTANT:
            - Be concise in Teams messages — executives read them quickly.
            - For email subjects use: "DailyValuations Comparison — {result.ValuationDate} — {result.Status}" format.
            - If a tool returns ERROR, log it in your reply but do not retry.
            - Do NOT invent data; use only what is in the result JSON provided.
            """
        };

        private static OllamaMessage UserMessage(string resultJson) => new()
        {
            Role = "user",
            Content = $"Here is the comparison result. Please render and dispatch the report:\n\n{resultJson}"
        };


        private async Task<OllamaMessage> CallOllamaAsync(
            List<OllamaMessage> messages,
            List<OllamaTool> tools,
            CancellationToken ct)
        {
            var request = new OllamaChatRequest
            {
                Model = _agent.ModelName,
                Messages = messages,
                Tools = tools,
                Stream = false,
                Options = new OllamaOptions { Temperature = 0.2, NumCtx = _agent.ContextSize }
            };

            var json = JsonSerializer.Serialize(request, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var http = await httpClient.PostAsync("/api/chat", content, ct);
            http.EnsureSuccessStatusCode();

            var body = await http.Content.ReadAsStringAsync(ct);
            var response = JsonSerializer.Deserialize<OllamaChatResponse>(body, JsonOpts)
                           ?? throw new InvalidOperationException("Null response from Ollama");

            return response.Message;
        }

        private async Task<string> InvokeMcpToolAsync(OllamaToolCall toolCall, CancellationToken ct)
        {
            try
            {
                var args = toolCall.Function.Arguments.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        toolCall.Function.Arguments.GetRawText(), JsonOpts)
                    : null;

                var result = await mcpClient.CallToolAsync(
                    toolCall.Function.Name, args, cancellationToken: ct);

                return string.Join("\n", result.Content.OfType<TextContent>().Select(t => t.Text));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ReportingAgent tool call '{Tool}' failed", toolCall.Function.Name);
                return $"ERROR: {ex.Message}";
            }
        }

        private static OllamaTool ToOllamaTool(McpClientTool mcpTool)
        {
            var fallback = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}");
            var paramsJson = mcpTool.JsonSchema.ValueKind != JsonValueKind.Undefined
                ? mcpTool.JsonSchema
                : fallback;
            return new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = mcpTool.Name,
                    Description = mcpTool.Description ?? string.Empty,
                    Parameters = paramsJson
                }
            };
        }
    }
}
