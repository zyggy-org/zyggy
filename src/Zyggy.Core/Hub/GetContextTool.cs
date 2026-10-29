using System.Globalization;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Hub;

/// <summary>
/// <c>get_context(topic, max_tokens)</c> (spec 11 AC-3..AC-10): the arguments are checked before anything is read, then the principal's
/// durable files and the daily lines of the window are ranked and laid out under the budget. Read-only; reads at call time, no cache.
/// </summary>
internal sealed class GetContextTool(
    MemoryStore store,
    ContextOptions options,
    MemoryPaths paths,
    TimeZoneInfo zone,
    TimeProvider clock,
    TextWriter diagnostics) : McpServerTool
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = HubToolDescriptors.GetContextName,
        Description = HubToolDescriptors.GetContextDescription,
        InputSchema = HubToolDescriptors.GetContextSchema(),
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = ToolArguments.AsObject(request.Params?.Arguments);
        var refusal = ToolArguments.Refuse(arguments, HubToolDescriptors.GetContextArguments(), HubToolDescriptors.GetContextRequired, out var namedPrincipal);
        if (refusal is not null)
        {
            if (namedPrincipal)
            {
                await diagnostics.WriteAsync(ToolArguments.NamedPrincipalDiagnostic).ConfigureAwait(false);
                await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return Result(refusal, isError: true);
        }

        var maxTokens = options.DefaultMaxTokens;
        if (arguments.TryGetProperty("max_tokens", out var max) && !max.TryGetInt32(out maxTokens))
        {
            return Result(ToolArguments.Invalid, isError: true);
        }

        if (maxTokens < options.MinTokens || maxTokens > options.MaxTokens)
        {
            return Result(
                string.Create(CultureInfo.InvariantCulture, $"refused: invalid arguments: max_tokens must be {options.MinTokens}..{options.MaxTokens}"),
                isError: true);
        }

        var topic = arguments.GetProperty("topic").GetString()!;
        if (ContextRanker.Topic(topic, options).Refusal is { } topicRefusal)
        {
            return Result(topicRefusal, isError: true);
        }

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var result = ContextRanker.Rank(
            new ContextRequest(topic, maxTokens),
            store.DurableEntries(),
            store.DailyLines(today, options.DailyWindowDays),
            options,
            paths.Principal,
            now);
        return Result(result.Text, isError: false);
    }

    internal static CallToolResult Result(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };
}
