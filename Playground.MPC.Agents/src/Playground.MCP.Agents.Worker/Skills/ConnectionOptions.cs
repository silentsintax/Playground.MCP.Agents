namespace Playground.MCP.Agents.Worker.Skills
{
    public sealed class ConnectionOptions
    {
        public const string Section = "Databases";

        public string SourceConnectionString { get; set; } =
            "Server=localhost;Database=SourceDB;Integrated Security=true;TrustServerCertificate=true;";

        public string TargetConnectionString { get; set; } =
            "Server=localhost;Database=TargetDB;Integrated Security=true;TrustServerCertificate=true;";
    }
}
