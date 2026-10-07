using System.Text.Json;

namespace Zyggy.Core.M365.Runs;

/// <summary>The outcome of <see cref="RunMcpConfig.WriteM365Only"/>: the written file, or the configuration error (exit 3).</summary>
internal sealed record RunMcpConfigWrite(string? Path, string? Error);

/// <summary>
/// The MCP configuration an unattended m365 run loads (spec 36 AC-8, plan 36 Assumption 7): the checkout's <c>.mcp.json</c> reduced to its
/// <c>m365</c> entry, verbatim, written 0600 as <c>&lt;state&gt;/m365/run-mcp.json</c>, so the session's other servers (linkedin) are never
/// loaded by a run.
/// </summary>
internal static class RunMcpConfig
{
    public const string FileName = "run-mcp.json";

    public static RunMcpConfigWrite WriteM365Only(string checkout, M365Paths paths)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkout);
        ArgumentNullException.ThrowIfNull(paths);
        var source = Path.Join(checkout, ".mcp.json");
        var error = new RunMcpConfigWrite(null, $"configuration error: {source}: no m365 server");
        byte[] bytes;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(source));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty("m365", out var m365)
                || m365.ValueKind != JsonValueKind.Object)
            {
                return error;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("mcpServers");
                writer.WritePropertyName("m365");
                m365.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            bytes = [.. buffer.ToArray(), (byte)'\n'];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return error;
        }

        var target = paths.StateFile(FileName);
        StateFiles.WriteAtomically(paths.StateDirectory, target, bytes);
        return new RunMcpConfigWrite(target, null);
    }
}
