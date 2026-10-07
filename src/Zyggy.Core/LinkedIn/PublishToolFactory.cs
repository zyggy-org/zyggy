using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.LinkedIn;

/// <summary>The <c>publish_post</c> handler as the server holds it: one call per tool call, disposed with the server.</summary>
internal interface IPublishTool : IDisposable
{
    Task<ToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>The real handler with the HTTP exchange it owns.</summary>
internal sealed class OwnedPublishTool(PublishPostTool tool, LinkedInHttp http) : IPublishTool
{
    public Task<ToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken) => tool.CallAsync(arguments, cancellationToken);

    public void Dispose() => http.Dispose();
}

/// <summary>
/// The handler when the instance, the environment or the secret patterns are not usable: every call is
/// <c>configuration_error: &lt;message&gt;</c>, no request, one row when the state directory is usable.
/// </summary>
internal sealed class ConfigurationErrorTool(string message, LinkedInActionLog log, TimeProvider clock) : IPublishTool
{
    public Task<ToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var status = $"{LinkedInFailureWire.Token(LinkedInFailure.ConfigurationError)}: {message}";
        try
        {
            log.Append(new ActionRow(1, clock.GetUtcNow(), PublishPostTool.ToolName, null, string.Empty, 0, string.Empty, null, status));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The state directory is not usable either; the result still says why.
        }

        return Task.FromResult(new ToolCallResult(true, status));
    }

    public void Dispose()
    {
    }
}

/// <summary>Builds the <c>publish_post</c> handler from a verb context with the real credential store, HTTP, log and memory.</summary>
internal static class PublishToolFactory
{
    public static IPublishTool Create(LinkedInVerbContext context, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var log = new LinkedInActionLog(new LinkedInPaths(context.Environment));
        var (session, error) = LinkedInSession.Load(context);
        var endpoints = LinkedInEndpoints.Resolve(context.Environment);
        var patternsPath = SecretPatternsLocation.Resolve(context.Environment);
        var patterns = patternsPath is null ? null : SecretPatterns.Load(patternsPath);
        var problem = error
            ?? endpoints.Error
            ?? (patternsPath is null ? "configuration error: no secret patterns (ZYGGY_SECRET_PATTERNS or ZYGGY_INSTANCE_DIR)" : null)
            ?? (patterns!.Error is { } patternError ? "configuration error: " + patternError : null);
        if (problem is not null)
        {
            return new ConfigurationErrorTool(Strip(problem), log, context.Clock);
        }

        var http = new LinkedInHttp(context.LinkedInHandler, context.HttpTimeout, endpoints.Routes!.Loopback);
        var api = new LinkedInApi(http, endpoints.Routes, patterns!.Patterns);
        var tool = new PublishPostTool(
            session!.Configuration, session.Store, session.Tenant, api, log, patterns.Patterns!, session.Memory, session.TimeZone, context.Clock, diagnostics);
        return new OwnedPublishTool(tool, http);
    }

    // The wire token already says "configuration_error".
    private static string Strip(string message) =>
        message.StartsWith("configuration error: ", StringComparison.Ordinal) ? message["configuration error: ".Length..] : message;
}
