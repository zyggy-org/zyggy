using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zyggy.Core.Setup;

/// <summary>
/// The settings items of <c>zyggy setup</c> (spec 42 AC-13..AC-15, plan 42 A7): the user-scope Claude Code <c>settings.json</c> is created with
/// exactly <c>env.ZYGGY_SECRET_PATTERNS</c> and the three deny rules, or merged into additively — an existing env value always wins, the deny
/// rules are an ordinal set union appended after the existing entries, every other key, value and array element keeps its value and order.
/// The <c>hooks</c> key is never read for a decision nor written: it passes through untouched like every key this class does not name.
/// A file that is not valid JSON (or whose <c>env</c> / <c>permissions</c> / <c>permissions.deny</c> has the wrong shape) is left untouched
/// with no backup; otherwise one backup per run is written right before the one write (temp file, then replace), and a run that changes
/// nothing writes nothing. A UTF-8 BOM is read and never written back; an empty file counts as <c>{}</c>.
/// </summary>
internal static class ClaudeSettingsMerge
{
    /// <summary>The variable <c>zyggy context</c> and <c>zyggy archive</c> read the secret-pattern file from.</summary>
    public const string PatternsVariable = "ZYGGY_SECRET_PATTERNS";

    private const string EnvKey = "env";
    private const string PermissionsKey = "permissions";
    private const string DenyKey = "deny";
    private const string NotValid = "not valid JSON; left untouched";

    /// <summary>The three deny rules every node's user-scope settings carry (spec 42 AC-13), in the order they are appended.</summary>
    public static readonly string[] DenyRules = ["Bash(zyggy secret*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)"];

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // the owner's text stays as written (no ' for an apostrophe)
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates or merges <paramref name="settingsFile"/>; returns the env line and the deny line, or one failed line when the file is left untouched.</summary>
    /// <param name="settingsFile">The user-scope <c>settings.json</c>.</param>
    /// <param name="patternsFile">The value for <c>env.ZYGGY_SECRET_PATTERNS</c> (forward slashes).</param>
    /// <param name="stamp">The run's backup stamp.</param>
    public static IReadOnlyList<SetupLine> Apply(string settingsFile, string patternsFile, string stamp)
    {
        bool exists = File.Exists(settingsFile);
        byte[] original = [];
        if (exists)
        {
            try
            {
                original = File.ReadAllBytes(settingsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [Failed(settingsFile + " could not be read: " + ex.Message + "; left untouched")];
            }
        }

        if (Parse(original) is not { } root
            || !TryObject(root, EnvKey, out JsonObject? env)
            || !TryObject(root, PermissionsKey, out JsonObject? permissions)
            || (permissions?[DenyKey] is { } denyNode && denyNode is not JsonArray))
        {
            return [Failed(NotValid)];
        }

        var changed = false;
        SetupLine? envLine = MergeEnv(root, env, patternsFile, ref changed);
        string[] added = MergeDeny(root, permissions);
        changed |= added.Length > 0;
        if (!changed)
        {
            return [envLine!, new SetupLine(SetupItem.SettingsDeny, SetupOutcome.AlreadyDone, "permissions.deny holds the three zyggy rules")];
        }

        string? error = Write(settingsFile, root, exists ? original : null, stamp);
        if (error is not null)
        {
            return [Failed(settingsFile + " could not be written: " + error + "; left untouched")];
        }

        string suffix = exists ? " (backup " + SetupPaths.Backup(settingsFile, stamp) + ")" : " (created " + settingsFile + ")";
        return
        [
            envLine ?? new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Done, "env." + PatternsVariable + " set to " + patternsFile + suffix),
            added.Length > 0
                ? new SetupLine(SetupItem.SettingsDeny, SetupOutcome.Done, "permissions.deny gained " + string.Join(", ", added) + suffix)
                : new SetupLine(SetupItem.SettingsDeny, SetupOutcome.AlreadyDone, "permissions.deny holds the three zyggy rules"),
        ];
    }

    /// <summary>The env item: sets the variable when absent (returns <see langword="null"/>, the line is built after the write), else the line for the value found.</summary>
    private static SetupLine? MergeEnv(JsonObject root, JsonObject? env, string patternsFile, ref bool changed)
    {
        JsonNode? current = env?[PatternsVariable];
        if (current is null)
        {
            if (env is null)
            {
                root[EnvKey] = new JsonObject { [PatternsVariable] = patternsFile };
            }
            else
            {
                env[PatternsVariable] = patternsFile;
            }

            changed = true;
            return null;
        }

        string value = current is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : current.ToJsonString();
        if (string.Equals(value, patternsFile, StringComparison.Ordinal))
        {
            return new SetupLine(SetupItem.SettingsEnv, SetupOutcome.AlreadyDone, "env." + PatternsVariable + " is " + value);
        }

        return File.Exists(value)
            ? new SetupLine(SetupItem.SettingsEnv, SetupOutcome.LeftAsIs, PatternsVariable + " is " + value)
            : new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Failed, PatternsVariable + " is " + value + ", which does not exist; left as is");
    }

    /// <summary>Appends the missing deny rules (ordinal comparison) after the existing entries; returns the rules added.</summary>
    private static string[] MergeDeny(JsonObject root, JsonObject? permissions)
    {
        var deny = permissions?[DenyKey] as JsonArray;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? entry in deny ?? new JsonArray())
        {
            if (entry is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                present.Add(value.GetValue<string>());
            }
        }

        string[] missing = [.. DenyRules.Where(r => !present.Contains(r))];
        if (missing.Length == 0)
        {
            return missing;
        }

        if (deny is null)
        {
            deny = new JsonArray();
            if (permissions is null)
            {
                root[PermissionsKey] = new JsonObject { [DenyKey] = deny };
            }
            else
            {
                permissions[DenyKey] = deny;
            }
        }

        foreach (string rule in missing)
        {
            deny.Add(rule);
        }

        return missing;
    }

    /// <summary>
    /// Probes that the owner's file may be written (a read-only file is refused on every OS), writes the temp file, the backup, then replaces
    /// the original; on any failure the temp file and this run's backup are removed again and the original is untouched. Returns the error.
    /// </summary>
    private static string? Write(string settingsFile, JsonObject root, byte[]? original, string stamp)
    {
        string temp = SetupPaths.Temp(settingsFile, stamp);
        string backup = SetupPaths.Backup(settingsFile, stamp);
        var backupWritten = false;
        try
        {
            if (original is not null)
            {
                using (new FileStream(settingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    // Opened and closed without writing: neither content nor last-write time change.
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
            }

            File.WriteAllText(temp, root.ToJsonString(WriteOptions) + "\n", Utf8);
            if (original is not null)
            {
                File.WriteAllBytes(backup, original);
                backupWritten = true;
            }

            File.Move(temp, settingsFile, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            if (backupWritten)
            {
                TryDelete(backup);
            }

            return ex.Message;
        }
    }

    private static JsonObject? Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
        if (json.Trim(" \t\r\n"u8).IsEmpty)
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json, nodeOptions: null, DocumentOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="key"/> is absent (<paramref name="value"/> null) or an object; false for any other shape.</summary>
    private static bool TryObject(JsonObject root, string key, out JsonObject? value)
    {
        JsonNode? node = root[key];
        value = node as JsonObject;
        return node is null || value is not null;
    }

    private static SetupLine Failed(string detail) => new(SetupItem.SettingsEnv, SetupOutcome.Failed, detail);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This run's own temp or backup file only; the owner's file was never replaced.
        }
    }
}
