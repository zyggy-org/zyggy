using System.Text.Json;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zyggy.Core.LinkedIn.Mcp;

/// <summary>
/// <c>publish_post</c> as the SDK serves it: the protocol tool from <see cref="PublishPostDescriptor"/>, and an invoke that passes the raw
/// arguments to the handler, which re-validates them — the SDK's schema handling is never trusted for policy.
/// </summary>
internal sealed class PublishPostMcpTool(IPublishTool tool, int maxChars) : McpServerTool
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = PublishPostDescriptor.Name,
        Description = PublishPostDescriptor.Description,
        InputSchema = PublishPostDescriptor.InputSchema(maxChars),
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await tool.CallAsync(Arguments(request.Params?.Arguments), cancellationToken).ConfigureAwait(false);
        return new CallToolResult { IsError = result.IsError, Content = [new TextContentBlock { Text = result.Text }] };
    }

    // The arguments as one JSON object, exactly as received; absent arguments are an empty object (refused as invalid).
    private static JsonElement Arguments(IDictionary<string, JsonElement>? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
