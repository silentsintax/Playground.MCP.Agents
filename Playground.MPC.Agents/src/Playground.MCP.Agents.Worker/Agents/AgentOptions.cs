namespace Playground.MCP.Agents.Worker.Agents
{
    public sealed class AgentOptions
    {
        public const string Section = "Agent";

        /// <summary>Modelo do Ollama.</summary>
        public string ModelName { get; set; } = "llama3.1";

        /// <summary>Ollama URL.</summary>
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

        /// <summary>Tamnho da janela de contexto.</summary>
        public int ContextSize { get; set; } = 8192;

        /// <summary>label da base de origem</summary>
        public string SourceLabel { get; set; } = "Source";

        /// <summary>label da base de destino.</summary>
        public string TargetLabel { get; set; } = "Target";
    }
}
