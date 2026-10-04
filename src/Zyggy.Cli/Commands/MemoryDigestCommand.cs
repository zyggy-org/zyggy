using System.CommandLine;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy memory digest &lt;identity|index|daily&gt;</c>: the SessionStart hook's digest (27 hook contract). Exit 0 with no output
/// when <c>ZYGGY_HOOKS=off</c>; 3 for a configuration error; 4 for an unknown section. Built without a host so a session start
/// stays well inside the hook timeout.
/// </summary>
internal static class MemoryDigestCommand
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // A hook closes stdin after writing its JSON; a run by hand may leave it open. Never wait longer than this for it.
    private static readonly TimeSpan StdinWait = TimeSpan.FromSeconds(2);

    public static Command Create(CliEnvironment environment)
    {
        var section = new Argument<string>("section") { Description = "identity, index or daily" };
        var digest = new Command("digest", "Print one memory digest section for a session start.") { section };
        digest.SetAction((parseResult, cancellationToken) => RunAsync(environment, parseResult.GetValue(section) ?? string.Empty, cancellationToken));
        return digest;
    }

    public static async Task<int> RunAsync(CliEnvironment environment, string sectionName, CancellationToken cancellationToken)
    {
        if (environment.HooksOff)
        {
            return ExitCodes.Ok;
        }

        if (Configure(environment) is not { } paths)
        {
            return ExitCodes.Configuration;
        }

        DigestSection? section = sectionName switch
        {
            "identity" => DigestSection.Identity,
            "index" => DigestSection.Index,
            "daily" => DigestSection.Daily,
            _ => null,
        };
        if (section is null)
        {
            await ErrorAsync($"unknown section '{sectionName}' (expected identity, index or daily)").ConfigureAwait(false);
            return ExitCodes.UnknownSection;
        }

        var options = new DigestOptions
        {
            IdentityBytes = DigestOptions.ParseCap(environment.Get("ZYGGY_DIGEST_BYTES_IDENTITY"), DigestSection.Identity),
            IndexBytes = DigestOptions.ParseCap(environment.Get("ZYGGY_DIGEST_BYTES_INDEX"), DigestSection.Index),
            DailyBytes = DigestOptions.ParseCap(environment.Get("ZYGGY_DIGEST_BYTES_DAILY"), DigestSection.Daily),
        };
        var cwd = await HookCwdAsync(cancellationToken).ConfigureAwait(false);
        var start = cwd ?? environment.Get("CLAUDE_PROJECT_DIR") ?? Environment.CurrentDirectory;
        var output = new DigestBuilder(paths, options, TimeProvider.System).Build(section.Value, start);

        await using (var stdout = Console.OpenStandardOutput())
        {
            await stdout.WriteAsync(Utf8NoBom.GetBytes(output.Text), cancellationToken).ConfigureAwait(false);
        }

        foreach (var line in output.StderrLines)
        {
            await WriteStderrAsync(line).ConfigureAwait(false);
        }

        return ExitCodes.Ok;
    }

    private static MemoryPaths? Configure(CliEnvironment environment)
    {
        foreach (var variable in new[] { "ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER" })
        {
            if (environment.Get(variable) is null)
            {
                return ConfigurationError($"{variable} is not set");
            }
        }

        var root = environment.Get("ZYGGY_MEMORY_ROOT")!;
        if (!Directory.Exists(root))
        {
            return ConfigurationError($"memory root {root} does not exist (ZYGGY_MEMORY_ROOT)");
        }

        if (!TenantId.TryParse(environment.Get("ZYGGY_TENANT"), out var tenant))
        {
            return ConfigurationError($"ZYGGY_TENANT '{environment.Get("ZYGGY_TENANT")}' is not a valid label");
        }

        if (!UserId.TryParse(environment.Get("ZYGGY_USER"), out var user))
        {
            return ConfigurationError($"ZYGGY_USER '{environment.Get("ZYGGY_USER")}' is not a valid label");
        }

        var paths = new MemoryPaths(root, new Principal(tenant, user));
        return Directory.Exists(paths.PrincipalDirectory)
            ? paths
            : ConfigurationError($"memory directory {paths.PrincipalDirectory} does not exist (ZYGGY_TENANT/ZYGGY_USER)");
    }

    private static MemoryPaths? ConfigurationError(string message)
    {
        WriteStderrAsync("zyggy: configuration error: " + message).GetAwaiter().GetResult();
        return null;
    }

    private static Task ErrorAsync(string message) => WriteStderrAsync("zyggy: " + message);

    private static async Task WriteStderrAsync(string line)
    {
        await using var stderr = Console.OpenStandardError();
        await stderr.WriteAsync(Utf8NoBom.GetBytes(line + "\n")).ConfigureAwait(false);
    }

    // The hook's JSON "cwd", read only when stdin is redirected and only for a bounded time.
    private static async Task<string?> HookCwdAsync(CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        var read = Task.Run(Console.In.ReadToEnd, cancellationToken);
        if (await Task.WhenAny(read, Task.Delay(StdinWait, cancellationToken)).ConfigureAwait(false) != read)
        {
            return null;
        }

        var json = await read.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("cwd", out var cwd)
                && cwd.ValueKind == JsonValueKind.String
                && cwd.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
