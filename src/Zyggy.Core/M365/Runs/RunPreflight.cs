using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using Zyggy.Core.M365.Tools;
using Zyggy.Core.Models;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365.Runs;

/// <summary>What a model-run verb has once its pre-flight passed.</summary>
internal sealed record RunSetup(M365Session Session, M365ToolPartition Partition, IModelRunner Model);

/// <summary>
/// The pre-flight of the brief and the backfills, before any request: the principal (optionally from the settings file, the backfills),
/// <c>instance/m365.json</c>, the tool partition, the run's m365-only MCP configuration, <c>claude</c> resolvable (<c>ZYGGY_CLAUDE_PATH</c> or <c>PATH</c>) and the binary pin.
/// </summary>
internal static class RunPreflight
{
    public static (RunSetup? Setup, int Exit, string? Error, string? Warning) Load(M365VerbContext context, bool principalFromSettings)
    {
        ArgumentNullException.ThrowIfNull(context);
        var effective = context;
        if (principalFromSettings && M365Environment.Load(context.Environment).Environment is { } instance)
        {
            effective = context with { Environment = M365Environment.PrincipalFromSettings(context.Environment, instance.SettingsPath) };
        }

        var (session, exit, error) = M365Session.Load(effective, baseOnly: false);
        if (session is null)
        {
            return (null, exit, error, null);
        }

        if (M365ToolPartition.Load(session.Instance.Checkout) is not { Partition: { } partition } load)
        {
            return (null, 3, M365ToolPartition.Load(session.Instance.Checkout).Error, session.Warning);
        }

        // Spec 36 AC-8: the run loads only the m365 server, never the session's others (linkedin).
        try
        {
            if (RunMcpConfig.WriteM365Only(session.Instance.Checkout, session.Instance.Paths).Error is { } mcpError)
            {
                return (null, 3, mcpError, session.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 3, $"configuration error: {ex.Message}", session.Warning);
        }

        var claude = Value(context.Environment, "ZYGGY_CLAUDE_PATH") ?? ProgramLocator.OnPath("claude", context.Environment);
        if (claude is null || (Path.IsPathRooted(claude) && !File.Exists(claude)))
        {
            return (null, 3, "claude not found", session.Warning);
        }

        if (context.CheckBinaryPin
            && BinaryPin.Check(Value(context.Environment, "ZYGGY_INSTANCE_DIR"), Version(), BinaryHash(), RuntimeInformation.RuntimeIdentifier, ReadFile) is { } pinError)
        {
            return (null, 3, pinError, session.Warning);
        }

        var model = context.ModelRunnerFactory?.Invoke(claude)
            ?? new ClaudeCodeCliRunner(context.Runner, Options.Create(new ClaudeCodeOptions { Path = claude }), context.Clock);
        return (new RunSetup(session, partition, model), 0, null, session.Warning);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static string Version() =>
        (Assembly.GetEntryAssembly() ?? typeof(RunPreflight).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static string BinaryHash()
    {
        var binary = Environment.ProcessPath ?? string.Empty;
        return File.Exists(binary) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary))) : string.Empty;
    }
}
