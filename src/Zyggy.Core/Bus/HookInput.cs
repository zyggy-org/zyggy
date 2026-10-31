using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>What the <c>Stop</c> hook keeps of a turn (spec 12 D2): the session, the working directory and the first non-empty line of the last message, collapsed and cut.</summary>
/// <param name="SessionId">The session id, safe as a file name.</param>
/// <param name="Cwd">The session's working directory as Claude Code reports it.</param>
/// <param name="FirstLine">The note: the first non-empty line, whitespace collapsed, at most the configured length with <c>…</c>.</param>
public sealed record StopHookNote(string SessionId, string Cwd, string FirstLine);

/// <summary>Reads the <c>Stop</c> hook JSON a laptop's Claude Code writes on stdin, as the template's <c>stop.sh</c> reads it.</summary>
public static partial class StopHookInput
{
    /// <summary>How the input was classified: a note, nothing to note, or text that is not a hook JSON object.</summary>
    internal enum Status
    {
        Note,
        Ignored,
        Unparseable,
    }

    /// <summary>
    /// Parses the hook JSON. <see langword="null"/> when the input is not a JSON object, <c>stop_hook_active</c> is true, the message
    /// has no non-empty line, or the session id is unsafe as a file name.
    /// </summary>
    /// <param name="stdin">The hook's standard input.</param>
    /// <param name="options">The note length.</param>
    /// <returns>The note, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static StopHookNote? Parse(TextReader stdin, ContextEnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(options);
        return Read(stdin.ReadToEnd(), options, out _);
    }

    /// <summary>Parses the text and says why nothing came back.</summary>
    internal static StopHookNote? Read(string text, ContextEnvelopeOptions options, out Status status)
    {
        StopHookEnvelope? envelope = HookJsonContext.Deserialize<StopHookEnvelope>(text);
        if (envelope is null)
        {
            status = Status.Unparseable;
            return null;
        }

        status = Status.Ignored;
        if (envelope.StopHookActive == true || !IsSafeSessionId(envelope.SessionId))
        {
            return null;
        }

        string? note = FirstNonEmptyLine(envelope.LastAssistantMessage ?? string.Empty);
        if (note is null)
        {
            return null;
        }

        status = Status.Note;
        return new StopHookNote(envelope.SessionId!, envelope.Cwd ?? string.Empty, Cut(note, options.MaxNoteChars));
    }

    /// <summary>A session id that is safe as a file name: <c>[A-Za-z0-9-]{1,128}</c>.</summary>
    internal static bool IsSafeSessionId(string? sessionId) => sessionId is not null && SafeSessionId().IsMatch(sessionId);

    private static string? FirstNonEmptyLine(string message)
    {
        foreach (string raw in message.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            string line = TextCollapse.Line(raw);
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>The template's rule: over the limit, keep one fewer scalar and append <c>…</c>; at the limit, unchanged.</summary>
    private static string Cut(string note, int maxChars)
    {
        if (TextCollapse.CharCount(note) <= maxChars)
        {
            return note;
        }

        var builder = new StringBuilder(note.Length);
        int count = 0;
        foreach (Rune rune in note.EnumerateRunes())
        {
            if (count++ == maxChars - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.Append('…').ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSessionId();
}

/// <summary>Reads the <c>SessionEnd</c> hook JSON: only the session id matters.</summary>
public static class SessionEndInput
{
    /// <summary>The session id, or <see langword="null"/> when the input is not a hook JSON object or the id is absent or unsafe.</summary>
    /// <param name="stdin">The hook's standard input.</param>
    /// <returns>The session id, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stdin"/> is null.</exception>
    public static string? SessionId(TextReader stdin)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        SessionEndEnvelope? envelope = HookJsonContext.Deserialize<SessionEndEnvelope>(stdin.ReadToEnd());
        return envelope is not null && StopHookInput.IsSafeSessionId(envelope.SessionId) ? envelope.SessionId : null;
    }
}

/// <summary>The members of the <c>Stop</c> hook JSON this product reads; every other member is ignored.</summary>
internal sealed class StopHookEnvelope
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("stop_hook_active")]
    public bool? StopHookActive { get; set; }

    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; set; }
}

/// <summary>The member of the <c>SessionEnd</c> hook JSON this product reads.</summary>
internal sealed class SessionEndEnvelope
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StopHookEnvelope))]
[JsonSerializable(typeof(SessionEndEnvelope))]
internal sealed partial class HookJsonContext : JsonSerializerContext
{
    /// <summary>Deserialises a hook JSON object; <see langword="null"/> for anything that is not one.</summary>
    public static T? Deserialize<T>(string text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(text, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Default.GetTypeInfo(typeof(T))!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
