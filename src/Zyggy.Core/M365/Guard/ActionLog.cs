using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.M365.Guard;

/// <summary>
/// The PostToolUse action log — <c>m365-log.sh</c> (spec 23 D7, spec 33 AC-22): one body-free row
/// <c>{ts, session_id, tool, summary, status}</c> per call of an action tool in <c>actions.jsonl</c> (0600 in the 0700 state directory),
/// appended under an exclusive lock. The summary names what the owner allowed without its content.
/// </summary>
internal sealed partial class ActionLog(string stateDirectory)
{
    private const string ToolPrefix = "mcp__m365__";
    private const int LockAttempts = 50;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string FilePath => Path.Join(stateDirectory, "actions.jsonl");

    /// <summary>The row for a PostToolUse input, or <see langword="null"/> for a tool that is not an action tool.</summary>
    public static string? BuildRow(JsonElement hook, DateTimeOffset now)
    {
        var tool = hook.TryGetProperty("tool_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
        if (GuardPolicy.ActionOf(tool) is not { } action)
        {
            return null;
        }

        var input = hook.TryGetProperty("tool_input", out var i) ? i : default;
        var response = hook.TryGetProperty("tool_response", out var r) ? r : default;
        var session = hook.TryGetProperty("session_id", out var s) && JsonView.IsTruthy(s) ? ToText(s) : string.Empty;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("session_id", session);
            writer.WriteString("tool", tool[ToolPrefix.Length..]);
            writer.WriteString("summary", Summary(action, input));
            writer.WriteString("status", Status(response));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary><c>zy_m365_append</c>: one line, 0600, the state directory 0700, an exclusive lock held for the append.</summary>
    /// <exception cref="IOException">Thrown when the row cannot be appended.</exception>
    public void Append(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        EnsureDirectory();
        var bytes = Encoding.UTF8.GetBytes(row + "\n");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(FilePath, options))
                {
                    stream.Write(bytes);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return;
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void EnsureDirectory()
    {
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(stateDirectory);
            return;
        }

        if (!Directory.Exists(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory, Owner);
        }

        File.SetUnixFileMode(stateDirectory, Owner);
    }

    // The jq program of m365-log.sh, from tool_input only: never the body or the content.
    private static string Summary(GuardAction action, JsonElement input)
    {
        JsonElement? Field(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) ? v : null;
        static JsonElement? Ci(JsonElement? element, string key) => JsonView.Of(element)?.CiGet(key);

        switch (action)
        {
            case GuardAction.Send:
                {
                    var message = Ci(Field("body"), "message");
                    var addresses = Elements(OrEmptyList(Ci(message, "torecipients"))).Concat(Elements(OrEmptyList(Ci(message, "ccrecipients"))))
                        .Select(r => Flat(OrEmpty(JsonView.Of(JsonView.Of(r)?.Get("emailAddress"))?.Get("address"))));
                    var subject = CodePoints(Flat(OrEmpty(Ci(message, "subject"))), 120);
                    var length = JqLength(OrEmptyValue(Ci(Ci(message, "body"), "content")));
                    return $"to {string.Join(',', addresses)} subject \"{subject}\" body {length} chars";
                }

            case GuardAction.Upload:
                {
                    var item = Flat(OrEmpty(Field("driveItemId")));
                    var form = NewFileForm().Match(item);
                    var (parent, name) = form.Success ? (form.Groups["p"].Value, form.Groups["n"].Value) : (item, string.Empty);
                    var content = Flat(OrEmpty(Field("body"))).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
                    var padding = content.EndsWith("==", StringComparison.Ordinal) ? 2 : content.EndsWith('=') ? 1 : 0;
                    var size = (long)Math.Floor((content.EnumerateRunes().Count() * 3.0 / 4) - padding);
                    return $"drive {Flat(OrEmpty(Field("driveId")))} parent {parent} name {name} size {size}";
                }

            default:
                return $"message {Flat(OrEmpty(Field("messageId")))} -> {Flat(OrEmpty(Ci(Field("body"), "destinationid")))}";
        }
    }

    // An MCP error result, or a Graph error object in the result text, is "error: <code>".
    private static string Status(JsonElement response)
    {
        JsonElement? error = null;
        foreach (var obj in Objects(response))
        {
            if (obj.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && ParseObject(text.GetString()!) is { } parsed
                && parsed.TryGetProperty("error", out var e) && JsonView.IsTruthy(e))
            {
                error = e.Clone();
                break;
            }
        }

        var isError = response.ValueKind == JsonValueKind.Object
            && (Truthy(response, "isError") ?? Truthy(response, "is_error")) is { ValueKind: JsonValueKind.True };
        if (isError)
        {
            return "error: " + Code(error is { ValueKind: JsonValueKind.Object } o ? CodeOrStatus(o) : "unknown");
        }

        if (error is { } found)
        {
            return "error: " + Code(found.ValueKind == JsonValueKind.Object ? CodeOrStatus(found) : ToText(found));
        }

        return "ok";
    }

    private static string CodeOrStatus(JsonElement error) =>
        error.TryGetProperty("code", out var code) && JsonView.IsTruthy(code) ? ToText(code)
        : error.TryGetProperty("status", out var status) && JsonView.IsTruthy(status) ? ToText(status)
        : "unknown";

    // def code: tostring | gsub("[^A-Za-z0-9_.-]"; "") | .[:40] | if . == "" then "unknown" else . end
    private static string Code(string text)
    {
        var cleaned = CodeCharacters().Replace(text, string.Empty);
        cleaned = cleaned.Length > 40 ? cleaned[..40] : cleaned;
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    private static JsonElement? Truthy(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && JsonView.IsTruthy(value) ? value : null;

    // jq `..`: every value in pre-order; the objects among them.
    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var inner in Objects(property.Value))
                {
                    yield return inner;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var inner in Objects(item))
                {
                    yield return inner;
                }
            }
        }
    }

    private static JsonElement? ParseObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // (x // []) — null, false or missing is the empty list.
    private static JsonElement? OrEmptyList(JsonElement? element) => JsonView.IsTruthy(element) ? element : null;

    private static IEnumerable<JsonElement> Elements(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Array } a => a.EnumerateArray(),
        { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().Select(p => p.Value),
        _ => [],
    };

    // (x // "") as text
    private static string OrEmpty(JsonElement? element) => JsonView.IsTruthy(element) ? ToText(element!.Value) : string.Empty;

    private static JsonElement? OrEmptyValue(JsonElement? element) => JsonView.IsTruthy(element) ? element : null;

    // jq length: a string's code points, an array's or object's entries, a number's absolute value; null (or "") 0.
    private static long JqLength(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString()!.EnumerateRunes().Count(),
        { ValueKind: JsonValueKind.Array } a => a.GetArrayLength(),
        { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().Count(),
        { ValueKind: JsonValueKind.Number } n => (long)Math.Abs(n.GetDouble()),
        _ => 0,
    };

    // tostring: a string as is, anything else its JSON text.
    private static string ToText(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();

    // def flat: tostring | gsub("[[:cntrl:]]"; " ")
    private static string Flat(string text) => Controls().Replace(text, " ");

    private static string CodePoints(string text, int count) => string.Concat(text.EnumerateRunes().Take(count).Select(r => r.ToString()));

    [GeneratedRegex("[\\u0000-\\u001f\\u007f-\\u009f]", RegexOptions.CultureInvariant)]
    private static partial Regex Controls();

    [GeneratedRegex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex CodeCharacters();

    [GeneratedRegex("\\A(?<p>[^:]*):/(?<n>.*):\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NewFileForm();
}
