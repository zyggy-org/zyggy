using System.Text.Json;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Dream;

/// <summary>The outcome of <see cref="DreamConfiguration.Load"/>: a complete configuration, or the key that is missing or invalid.</summary>
/// <param name="Environment">The run environment when valid.</param>
/// <param name="Options">The thresholds when valid.</param>
/// <param name="PinJson">The content of <c>instance/zyggy.json</c> when an instance directory is set.</param>
/// <param name="ErrorKey">The offending key (an environment variable, a <c>dream.json</c> key or a file name).</param>
/// <param name="Error">A one-line message naming the key.</param>
public sealed record DreamConfigurationResult(
    DreamEnvironment? Environment,
    DreamOptions? Options,
    string? PinJson,
    string? ErrorKey,
    string? Error)
{
    internal static DreamConfigurationResult Fail(string key, string message) => new(null, null, null, key, message);
}

/// <summary>
/// Reads the dream configuration (spec 28 Configuration): the principal and memory root from the environment, the time zone,
/// the state directory, the secret-pattern file (fail closed) and, when <c>ZYGGY_INSTANCE_DIR</c> is set, the pin and the optional
/// <c>dream.json</c> over the code defaults within their ceilings.
/// </summary>
public static class DreamConfiguration
{
    /// <summary>Loads the configuration.</summary>
    /// <param name="environment">The process environment.</param>
    /// <param name="version">The running <c>zyggy</c> version.</param>
    /// <param name="readFile">Returns a file's text, or <see langword="null"/> when it does not exist.</param>
    /// <returns>The configuration, or the first error.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static DreamConfigurationResult Load(IReadOnlyDictionary<string, string?> environment, string version, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readFile);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        foreach (var key in new[] { "ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER" })
        {
            if (Get(key) is null)
            {
                return DreamConfigurationResult.Fail(key, $"{key} is not set");
            }
        }

        var root = Get("ZYGGY_MEMORY_ROOT")!;
        if (!Directory.Exists(root))
        {
            return DreamConfigurationResult.Fail("ZYGGY_MEMORY_ROOT", $"memory root {root} does not exist (ZYGGY_MEMORY_ROOT)");
        }

        if (!TenantId.TryParse(Get("ZYGGY_TENANT"), out var tenant))
        {
            return DreamConfigurationResult.Fail("ZYGGY_TENANT", $"ZYGGY_TENANT '{Get("ZYGGY_TENANT")}' is not a valid label");
        }

        if (!UserId.TryParse(Get("ZYGGY_USER"), out var user))
        {
            return DreamConfigurationResult.Fail("ZYGGY_USER", $"ZYGGY_USER '{Get("ZYGGY_USER")}' is not a valid label");
        }

        var principal = new Principal(tenant, user);
        var principalDirectory = Path.Join(root, tenant.Value, user.Value);
        if (!Directory.Exists(principalDirectory))
        {
            return DreamConfigurationResult.Fail("ZYGGY_USER", $"memory directory {principalDirectory} does not exist (ZYGGY_TENANT/ZYGGY_USER)");
        }

        var zoneId = Get("ZYGGY_TIMEZONE") ?? "UTC";
        TimeZoneInfo zone;
        try
        {
            zone = zoneId == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DreamConfigurationResult.Fail("ZYGGY_TIMEZONE", $"ZYGGY_TIMEZONE '{zoneId}' is not a known time zone");
        }

        var instance = Get("ZYGGY_INSTANCE_DIR");
        var patterns = Get("ZYGGY_SECRET_PATTERNS")
            ?? (instance is null ? null : Path.GetFullPath(Path.Join(instance, "..", ".claude", "hooks", "secret-patterns.txt")));
        if (patterns is null)
        {
            return DreamConfigurationResult.Fail("ZYGGY_SECRET_PATTERNS", "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR to derive it from)");
        }

        if (!File.Exists(patterns))
        {
            return DreamConfigurationResult.Fail("ZYGGY_SECRET_PATTERNS", $"secret patterns {patterns} are missing (ZYGGY_SECRET_PATTERNS)");
        }

        var state = Get("ZYGGY_STATE_DIR")
            ?? Path.Join(Get("HOME") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "state", "zyggy");

        var options = new DreamOptions();
        string? pin = null;
        if (instance is not null)
        {
            pin = readFile(Path.Join(instance, "zyggy.json"));
            if (pin is null)
            {
                return DreamConfigurationResult.Fail("zyggy.json", $"{Path.Join(instance, "zyggy.json")} is missing (the version pin)");
            }

            if (readFile(Path.Join(instance, "dream.json")) is { } json)
            {
                var parsed = ParseOptions(json, options);
                if (parsed.Key is not null)
                {
                    return DreamConfigurationResult.Fail(parsed.Key, $"instance/dream.json: {parsed.Key} {parsed.Message}");
                }

                options = parsed.Options!;
            }
        }

        if (options.Validate() is [var offending, ..])
        {
            return DreamConfigurationResult.Fail(offending, $"instance/dream.json: {offending} is above its ceiling or below its minimum");
        }

        return new DreamConfigurationResult(
            new DreamEnvironment(root, principal, zone, state, patterns, instance, version), options, pin, null, null);
    }

    private static (DreamOptions? Options, string? Key, string? Message) ParseOptions(string json, DreamOptions options)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, "dream.json", "is not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "dream.json", "is not a JSON object");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                try
                {
                    options = property.Name switch
                    {
                        "batchMaxLines" => options with { BatchMaxLines = value.GetInt32() },
                        "batchMinLines" => options with { BatchMinLines = value.GetInt32() },
                        "batchMaxBytes" => options with { BatchMaxBytes = value.GetInt32() },
                        "maxBatchesPerRun" => options with { MaxBatchesPerRun = value.GetInt32() },
                        "callTimeoutMinutes" => options with { CallTimeoutMinutes = value.GetInt32() },
                        "callMaxTurns" => options with { CallMaxTurns = value.GetInt32() },
                        "callMaxBudgetUsd" => options with { CallMaxBudgetUsd = value.GetDecimal() },
                        "runMaxMinutes" => options with { RunMaxMinutes = value.GetInt32() },
                        "runMaxBudgetUsd" => options with { RunMaxBudgetUsd = value.GetDecimal() },
                        "compressAboveLines" => options with { CompressAboveLines = value.GetInt32() },
                        "compressMaxRemovedRatio" => options with { CompressMaxRemovedRatio = value.GetDouble() },
                        "maxCompressionsPerRun" => options with { MaxCompressionsPerRun = value.GetInt32() },
                        "batchMaxRemovedRatio" => options with { BatchMaxRemovedRatio = value.GetDouble() },
                        "batchMaxRemovedLines" => options with { BatchMaxRemovedLines = value.GetInt32() },
                        "runMaxRemovedRatio" => options with { RunMaxRemovedRatio = value.GetDouble() },
                        "identityMaxShrinkRatio" => options with { IdentityMaxShrinkRatio = value.GetDouble() },
                        "maxCategoriesPerSide" => options with { MaxCategoriesPerSide = value.GetInt32() },
                        "maxNewCategoriesPerRun" => options with { MaxNewCategoriesPerRun = value.GetInt32() },
                        "inboxDeleteGraceDays" => options with { InboxDeleteGraceDays = value.GetInt32() },
                        "dailyRollupDays" => options with { DailyRollupDays = value.GetInt32() },
                        "quarantineAfter" => options with { QuarantineAfter = value.GetInt32() },
                        "model" => options with { Model = value.GetString() },
                        _ => throw new KeyNotFoundException(),
                    };
                }
                catch (KeyNotFoundException)
                {
                    return (null, property.Name, "is not a dream.json key");
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    return (null, property.Name, "has the wrong type");
                }
            }
        }

        return (options, null, null);
    }
}
