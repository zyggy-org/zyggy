using System.Text.Json;

namespace Zyggy.Core.Memory;

/// <summary>The outcome of <see cref="ArchiveConfiguration.Load"/>: the options, or the key that is missing or invalid.</summary>
internal sealed record ArchiveConfigurationResult(ArchiveOptions? Options, string? ErrorKey, string? Error)
{
    public static ArchiveConfigurationResult Fail(string key, string message) => new(null, key, $"{key} {message}");
}

/// <summary>
/// Reads <c>instance/archive.json</c> (spec 37 Configuration): <c>ZYGGY_ARCHIVE_CONFIG</c>, else <c>&lt;ZYGGY_INSTANCE_DIR&gt;/archive.json</c>;
/// no file → the code defaults; a present file may only tighten, within the ceilings of <see cref="ArchiveOptions"/>.
/// </summary>
internal static class ArchiveConfiguration
{
    public static ArchiveConfigurationResult Load(IReadOnlyDictionary<string, string?> environment, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readFile);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var file = Get("ZYGGY_ARCHIVE_CONFIG") ?? (Get("ZYGGY_INSTANCE_DIR") is { } instance ? Path.Join(instance, "archive.json") : null);
        var options = new ArchiveOptions();
        if (file is not null && readFile(file) is { } json)
        {
            var home = Get("HOME") ?? Get("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var parsed = Parse(json, options, home);
            if (parsed.Key is not null)
            {
                return ArchiveConfigurationResult.Fail(parsed.Key, parsed.Message! + " (archive.json)");
            }

            options = parsed.Options!;
        }

        return options.Validate() is [var offending, ..]
            ? ArchiveConfigurationResult.Fail(offending, "is above its ceiling or below its minimum (archive.json)")
            : new ArchiveConfigurationResult(options, null, null);
    }

    private static (ArchiveOptions? Options, string? Key, string? Message) Parse(string json, ArchiveOptions options, string home)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, "archive.json", "is not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "archive.json", "is not a JSON object");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                try
                {
                    options = property.Name switch
                    {
                        "allowed_types" => options with { AllowedTypes = Types(value) },
                        "item_max_bytes" => options with { ItemMaxBytes = value.GetInt64() },
                        "project_max_bytes" => options with { ProjectMaxBytes = value.GetInt64() },
                        "total_max_bytes" => options with { TotalMaxBytes = value.GetInt64() },
                        "source_deny" => options with { SourceDeny = Strings(value).Select(entry => ExpandHome(entry, home)).ToArray() },
                        _ => throw new KeyNotFoundException(),
                    };
                }
                catch (KeyNotFoundException)
                {
                    return (null, property.Name, "is not an archive.json key");
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    return (null, property.Name, "has the wrong type");
                }
            }
        }

        return (options, null, null);
    }

    private static HashSet<ArchiveMediaType> Types(JsonElement value)
    {
        var types = new HashSet<ArchiveMediaType>();
        foreach (var wire in Strings(value))
        {
            if (!ArchiveMediaTypeWire.TryFromWire(wire, out var type))
            {
                throw new FormatException(wire);
            }

            types.Add(type);
        }

        return types;
    }

    private static string[] Strings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("not an array");
        }

        return value.EnumerateArray().Select(e => e.GetString() ?? throw new InvalidOperationException("null entry")).ToArray();
    }

    // "~" and "~/…" expand with the home directory; a fully qualified result is normalised to the platform's separators.
    private static string ExpandHome(string entry, string home)
    {
        var expanded = entry == "~" ? home
            : entry.StartsWith("~/", StringComparison.Ordinal) || entry.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(home, entry[2..])
            : entry;
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : expanded;
    }
}
