using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Core.Configuration;

namespace Zyggy.Core.Setup;

/// <summary>What <see cref="NodeJsonCompletion.Apply"/> did: the line, and the typed configuration when the completed file is accepted.</summary>
/// <param name="Line">The <see cref="SetupItem.NodeJson"/> line.</param>
/// <param name="Configuration">The completed file as the reader accepts it; <see langword="null"/> when refused (exit 3).</param>
internal sealed record NodeJsonResult(SetupLine Line, NodeConfiguration? Configuration);

/// <summary>
/// Completes an existing <c>node.json</c> backup-first (spec 42 AC-7..AC-12, plan 42 A3): only <c>claude.path</c> and <c>signWith</c> are ever
/// written. A refusal (invalid JSON, another tenant or machine, no <c>signWith</c>, the reader's refusal of the completed file) leaves the
/// file untouched with no backup. The completed file is written to a temp file and checked by <see cref="NodeConfigurationReader"/> before the
/// backup is taken and the original replaced; a run that changes nothing writes nothing.
/// </summary>
internal static class NodeJsonCompletion
{
    private const string ClaudeKey = "claude";
    private const string PathKey = "path";
    private const string SignWithKey = "signWith";
    private const string NoClaudeRemedy = "(the installer's claude.path pre-flight names the remedy)";

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static NodeJsonResult Apply(SetupRequest request, string? claudeExe)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = request.ConfigPath;
        if (!File.Exists(path))
        {
            return Refused("node.json not found at " + path);
        }

        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("node.json could not be read: " + ex.Message);
        }

        ReadOnlyMemory<byte> json = WithoutBom(original);
        JsonObject root;
        try
        {
            if (JsonNode.Parse(json.Span, nodeOptions: null, DocumentOptions) is not JsonObject parsed)
            {
                return Refused("node.json must be a JSON object");
            }

            root = parsed;
        }
        catch (JsonException ex)
        {
            return Refused("node.json is not valid JSON: " + ex.Message);
        }

        string? tenant = StringOf(root["tenant"]);
        string? machine = StringOf(root["machine"]);
        if ((tenant is not null && tenant != request.Tenant.Value) || (machine is not null && machine != request.Machine.Value))
        {
            return Refused($"node.json names tenant {tenant ?? "(none)"} and machine {machine ?? "(none)"}; the installer was given {request.Tenant.Value} and {request.Machine.Value}");
        }

        var changes = new List<string>();
        Note? note = CompleteClaudePath(root, claudeExe, changes);
        if (root[SignWithKey] is null)
        {
            if (request.SignWith is null)
            {
                return Refused($"signWith missing; run install.ps1 again with -SignWith {request.Tenant.Value}/<n>");
            }

            root[SignWithKey] = request.SignWith.ToString();
            changes.Add("added signWith");
        }

        if (changes.Count == 0)
        {
            NodeConfigurationResult unchanged = NodeConfigurationReader.Read(path);
            if (!unchanged.IsOk)
            {
                return Refused(unchanged.ToString());
            }

            SetupOutcome outcome = note is { LeftAsIs: true } ? SetupOutcome.LeftAsIs : SetupOutcome.AlreadyDone;
            return new NodeJsonResult(new SetupLine(SetupItem.NodeJson, outcome, note?.Text), unchanged.Configuration);
        }

        return Write(request, root, original, changes, note);
    }

    private static NodeJsonResult Write(SetupRequest request, JsonObject root, byte[] original, List<string> changes, Note? note)
    {
        string path = request.ConfigPath;
        string temp = SetupPaths.Temp(path, request.BackupStamp);
        string backup = SetupPaths.Backup(path, request.BackupStamp);
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions) + "\n", Utf8);
            NodeConfigurationResult completed = NodeConfigurationReader.Read(temp);
            if (!completed.IsOk)
            {
                File.Delete(temp);
                return Refused(completed.ToString());
            }

            File.WriteAllBytes(backup, original);
            File.Move(temp, path, overwrite: true);
            string detail = string.Join("; ", changes) + " (backup " + backup + ")";
            if (note is not null)
            {
                detail += "; " + note.Text;
            }

            if (HasComments(original))
            {
                detail += "; comments kept only in the backup";
            }

            return new NodeJsonResult(new SetupLine(SetupItem.NodeJson, SetupOutcome.Done, detail), completed.Configuration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return Refused("node.json could not be written: " + ex.Message);
        }
    }

    /// <summary>Adds or replaces <c>claude.path</c> when it is absent, null, or names a missing file and a <c>claude.exe</c> was found; otherwise a note or nothing.</summary>
    private static Note? CompleteClaudePath(JsonObject root, string? claudeExe, List<string> changes)
    {
        JsonNode? claude = root[ClaudeKey];
        if (claude is not null and not JsonObject)
        {
            return null; // the reader names the fault
        }

        JsonNode? current = (claude as JsonObject)?[PathKey];
        if (current is null)
        {
            if (claudeExe is null)
            {
                return new Note("claude.path not set; no claude.exe found " + NoClaudeRemedy, LeftAsIs: false);
            }

            if (claude is JsonObject section)
            {
                section[PathKey] = claudeExe;
            }
            else
            {
                root[ClaudeKey] = new JsonObject { [PathKey] = claudeExe };
            }

            changes.Add("added claude.path");
            return null;
        }

        if (StringOf(current) is not { Length: > 0 } old || File.Exists(old))
        {
            return null;
        }

        if (claudeExe is null)
        {
            return new Note($"claude.path {old} does not exist; no claude.exe found {NoClaudeRemedy}", LeftAsIs: true);
        }

        ((JsonObject)claude!)[PathKey] = claudeExe;
        changes.Add($"claude.path {old} did not exist; set to {claudeExe}");
        return null;
    }

    private static bool HasComments(byte[] original)
    {
        var reader = new Utf8JsonReader(WithoutBom(original).Span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsMemory(Encoding.UTF8.Preamble.Length) : bytes;

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static NodeJsonResult Refused(string detail) => new(new SetupLine(SetupItem.NodeJson, SetupOutcome.Failed, detail), null);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Our own temp file only; the owner's file was never replaced.
        }
    }

    private sealed record Note(string Text, bool LeftAsIs);
}
