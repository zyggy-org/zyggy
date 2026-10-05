using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// A backfill checkpoint (<c>mail-backfill.json</c>, <c>files-backfill.json</c>) as the scripts' jq programs keep it: one compact JSON
/// object, 0600, written as <c>.tmp</c> and renamed. Unknown fields are kept; untouched numbers keep their text; additions are jq's
/// (double) arithmetic.
/// </summary>
internal sealed class BackfillCheckpoint
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    private readonly string _path;
    private readonly string _stateDirectory;

    private BackfillCheckpoint(string path, string stateDirectory, JsonObject root)
    {
        _path = path;
        _stateDirectory = stateDirectory;
        Root = root;
    }

    public JsonObject Root { get; }

    /// <summary>
    /// Loads the checkpoint, or starts a new one from <paramref name="fresh"/>. An existing file that is not an object with a
    /// <paramref name="itemsKey"/> object is the error (exit 3).
    /// </summary>
    public static (BackfillCheckpoint? Checkpoint, string? Error) Load(string path, string stateDirectory, string itemsKey, string verb, Func<JsonObject> fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (!File.Exists(path))
        {
            return (new BackfillCheckpoint(path, stateDirectory, fresh()), null);
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root[itemsKey] is JsonObject)
            {
                return (new BackfillCheckpoint(path, stateDirectory, root), null);
            }
        }
        catch (JsonException)
        {
            // reported below
        }

        return (null, $"configuration error: {path} is not a checkpoint (zyggy m365 {verb} --reset starts again)");
    }

    public JsonObject Items(string itemsKey) => (JsonObject)Root[itemsKey]!;

    public void Write() => StateFiles.WriteAtomically(_stateDirectory, _path, System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(Compact) + "\n"));

    /// <summary><c>.key += value</c>: a number field increased (jq arithmetic on doubles).</summary>
    public static void Add(JsonObject target, string key, double value) => target[key] = JsonValue.Create(Number(target[key]) + value);

    public static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    public static long Long(JsonNode? node) => (long)Number(node);

    /// <summary><c>printf '%.2f'</c>.</summary>
    public static string Money(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>jq <c>-r</c> of a number: its JSON text (an integer without a fraction).</summary>
    public static string Text(JsonNode? node) => node?.ToJsonString() ?? "0";
}
