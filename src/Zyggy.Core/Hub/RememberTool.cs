using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Hub;

/// <summary>
/// <c>remember(fact, scope)</c> (spec 11 AC-9..AC-14, D4): the arguments are checked first, then parsed by the CLI's own
/// <see cref="RememberArguments"/> (same messages, always <c>[stated]</c>), then kept through <see cref="RememberService"/> — the same
/// inbox file, line and refusal as <c>zyggy memory remember</c>. A secret-shaped fact is refused by pattern name and never echoed.
/// Under <c>ZYGGY_HOOKS=off</c> nothing is stored.
/// </summary>
internal sealed class RememberTool(RememberService remember, bool hooksOff, TextWriter diagnostics) : McpServerTool
{
    public const string HooksOffText = "not stored: hooks are off (unattended run)";

    public override Tool ProtocolTool { get; } = new()
    {
        Name = HubToolDescriptors.RememberName,
        Description = HubToolDescriptors.RememberDescription,
        InputSchema = HubToolDescriptors.RememberSchema(),
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = ToolArguments.AsObject(request.Params?.Arguments);
        var refusal = ToolArguments.Refuse(arguments, HubToolDescriptors.RememberArguments(), HubToolDescriptors.RememberRequired, out var namedPrincipal);
        if (refusal is not null)
        {
            if (namedPrincipal)
            {
                await diagnostics.WriteAsync(ToolArguments.NamedPrincipalDiagnostic).ConfigureAwait(false);
                await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return GetContextTool.Result(refusal, isError: true);
        }

        var fact = arguments.GetProperty("fact").GetString()!;
        var scope = arguments.TryGetProperty("scope", out var value) ? value.GetString() : null;
        var parsed = RememberArguments.Parse(scope is null ? ["--", fact] : ["--scope", scope, "--", fact]);
        if (parsed.Error is not null)
        {
            return GetContextTool.Result("refused: " + parsed.Error, isError: true);
        }

        if (hooksOff)
        {
            return GetContextTool.Result(HooksOffText, isError: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outcome = remember.Remember(parsed.Request!);
        return outcome.RefusedBy is not null
            ? GetContextTool.Result($"refused: matches secret pattern {outcome.RefusedBy}", isError: true)
            : GetContextTool.Result($"remembered: {outcome.Path}\n{outcome.Line}\n", isError: false);
    }
}
