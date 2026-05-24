using Microsoft.Data.SqlClient;

namespace Playground.MCP.Agents.MCPServer
{
    public sealed class SqlConnectionFactory
    {
        public string SourceConnectionString { get; } =
            Environment.GetEnvironmentVariable("SOURCE_CONN")
            ?? throw new InvalidOperationException("SOURCE_CONN env var not set");

        public string TargetConnectionString { get; } =
            Environment.GetEnvironmentVariable("TARGET_CONN")
            ?? throw new InvalidOperationException("TARGET_CONN env var not set");

        public SqlConnection OpenSource() => Open(SourceConnectionString);
        public SqlConnection OpenTarget() => Open(TargetConnectionString);

        private static SqlConnection Open(string cs)
        {
            var conn = new SqlConnection(cs);
            conn.Open();
            return conn;
        }
    }
}
