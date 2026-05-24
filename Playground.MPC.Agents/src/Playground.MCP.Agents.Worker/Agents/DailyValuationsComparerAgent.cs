using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Playground.MCP.Agents.Code.Models;
using System.Text;
using System.Text.Json;

namespace Playground.MCP.Agents.Worker.Agents
{
    public sealed class DailyValuationsComparerAgent(
        McpClient mcpClient, 
        HttpClient httpClient, 
        IOptions<AgentOptions> options, 
        ILogger<DailyValuationsComparerAgent> logger)
    {

        private readonly AgentOptions _opts = options.Value;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public async Task<ComparisonResult> RunAsync(CancellationToken ct = default) 
        {
            // 1. Obtem as ferramentas disponíveis no MCP
            var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: ct);
            var ollamaTools = mcpTools.Select(t => ToOllamaTool(t)).ToList();

            // 2. Monta os promtpts para o Ollama
            var messages = new List<OllamaMessage>
            {
                SystemMessage(),
                UserMessage()
            };

            // 3. ReAct loop (max iterations to prevent runaway) -- Para detalhes ler isso depois -> https://agent-patterns.readthedocs.io/en/stable/patterns/react.html
            const int maxIterations = 12;
            string? finalAnalysis = null;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                logger.LogInformation("Agent: iteration {Iter}", iter + 1);

                var response = await CallOllamaAsync(messages, ollamaTools, ct);
                messages.Add(response);

                // Calo naolocalize nenhuma Tool, o agente entendeu que tem a resposta final e encerra o loop
                if (response.ToolCalls is not { Count: > 0 })
                {
                    finalAnalysis = response.Content;
                    logger.LogInformation("Agent: final answer received");
                    break;
                }

                // Executa cada chamada de ferramenta solicitada pelo agente e adiciona a resposta como uma nova mensagem
                foreach (var toolCall in response.ToolCalls)
                {
                    logger.LogInformation("Agent: calling tool '{Tool}'", toolCall.Function.Name);

                    var toolResult = await InvokeMcpToolAsync(toolCall, ct);

                    logger.LogDebug("Agent: tool '{Tool}' result: {Result}",
                        toolCall.Function.Name,
                        toolResult.Length > 200 ? toolResult[..200] + "…" : toolResult);

                    messages.Add(new OllamaMessage
                    {
                        Role = "tool",
                        Content = toolResult,
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Function.Name
                    });
                }
            }

            finalAnalysis ??= "Agent reached max iterations without a final answer.";

            // 4. Faz o sumário final e constrói o resultado
            return BuildResult(finalAnalysis, messages);
        }

        private async Task<OllamaMessage> CallOllamaAsync(
           List<OllamaMessage> messages,
           List<OllamaTool> tools,
           CancellationToken ct)
        {
            var request = new OllamaChatRequest
            {
                Model = _opts.ModelName,
                Messages = messages,
                Tools = tools,
                Stream = false,
                Options = new OllamaOptions { Temperature = 0.1, NumCtx = _opts.ContextSize }
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
                    toolCall.Function.Name,
                    args,
                    cancellationToken: ct);

                // Extrai o texto da resposta do mcpClient
                var texts = result.Content
                    .OfType<TextContent>()
                    .Select(t => t.Text);

                return string.Join("\n", texts);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tool call '{Tool}' failed", toolCall.Function.Name);
                return $"ERROR: {ex.Message}";
            }
        }

        private static OllamaMessage SystemMessage() => new()
        {
            Role = "system",
            Content = """
            You are a specialized data quality agent for financial valuation systems.
            Your task is to compare DailyValuations data between a SOURCE database
            and a TARGET database, identify discrepancies, and produce a concise report.

            STRATEGY:
            1. Call GetAvailableDates to discover which valuation dates exist in each DB.
            2. Call GetTableStats on both source and target for the most recent common date.
            3. Call CompareTables for that date (use tolerance=0.01 for monetary fields).
            4. If discrepancies exist, call GetSecurityDetail for the top 3 affected securities.
            5. Summarize your findings clearly.

            FINAL ANSWER FORMAT (always end with this JSON block):
            ```json
            {
              "status": "OK" | "DISCREPANCIES_FOUND" | "DATE_MISMATCH",
              "valuationDate": "yyyy-MM-dd",
              "sourceRows": <int>,
              "targetRows": <int>,
              "discrepancyCount": <int>,
              "topIssues": ["issue1", "issue2"],
              "recommendation": "text"
            }
            ```
            """
        };

        private static OllamaMessage UserMessage() => new()
        {
            Role = "user",
            Content = $"Please compare the DailyValuations tables as of today ({DateTime.UtcNow:yyyy-MM-dd}). " +
                      "Focus on the most recent valuation date available in both databases."
        };

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

        private ComparisonResult BuildResult(string analysis, List<OllamaMessage> _)
        {
            // Try to parse the JSON block the agent was instructed to emit
            try
            {
                var jsonStart = analysis.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
                var jsonEnd = analysis.IndexOf("```", jsonStart + 7, StringComparison.OrdinalIgnoreCase);

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonText = analysis[(jsonStart + 7)..jsonEnd].Trim();
                    var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    var statusStr = root.TryGetProperty("status", out var s) ? s.GetString() : "OK";
                    var status = statusStr switch
                    {
                        "DISCREPANCIES_FOUND" => ComparisonStatus.SuccessWithDiscrepancies,
                        "DATE_MISMATCH" => ComparisonStatus.Failed,
                        _ => ComparisonStatus.Success
                    };

                    return new ComparisonResult
                    {
                        Status = status,
                        ValuationDate = root.TryGetProperty("valuationDate", out var d)
                                             ? DateOnly.TryParse(d.GetString(), out var dt) ? dt : null
                                             : null,
                        TotalSourceRows = root.TryGetProperty("sourceRows", out var sr) ? sr.GetInt32() : 0,
                        TotalTargetRows = root.TryGetProperty("targetRows", out var tr) ? tr.GetInt32() : 0,
                        DiscrepancyCount = root.TryGetProperty("discrepancyCount", out var dc) ? dc.GetInt32() : 0,
                        AgentAnalysis = analysis,
                        SourceLabel = _opts.SourceLabel,
                        TargetLabel = _opts.TargetLabel
                    };
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not parse structured JSON from agent response");
            }

            // Fallback: just wrap the raw analysis
            return new ComparisonResult
            {
                Status = ComparisonStatus.Success,
                AgentAnalysis = analysis,
                SourceLabel = _opts.SourceLabel,
                TargetLabel = _opts.TargetLabel
            };
        }
    }
}
