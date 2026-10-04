using System.Text.Json;

namespace Zyggy.Core.Dream;

/// <summary>Whether the running binary matches its pin.</summary>
public enum VersionPinStatus
{
    /// <summary>The version and the file hash for this runtime match.</summary>
    Match,

    /// <summary>The version, the hash or this runtime's entry differs.</summary>
    Mismatch,

    /// <summary>The pin file is not the expected JSON.</summary>
    Invalid,
}

/// <summary>The outcome of <see cref="VersionPin.Check"/>.</summary>
/// <param name="Status">Match, mismatch or invalid.</param>
/// <param name="Detail">What differs, for the one log line.</param>
public sealed record VersionPinResult(VersionPinStatus Status, string? Detail);

/// <summary>
/// AC-37: <c>instance/zyggy.json</c> = <c>{ "version": "&lt;semver&gt;", "sha256": { "&lt;rid&gt;": "&lt;hex&gt;" } }</c>; the dream
/// refuses to run before any model call unless its own version (build metadata ignored) and file hash match.
/// </summary>
public static class VersionPin
{
    /// <summary>Compares the running binary with the pin.</summary>
    /// <param name="pinJson">The content of <c>zyggy.json</c>.</param>
    /// <param name="runningVersion">The informational version of the running binary.</param>
    /// <param name="runningSha256">The lowercase SHA-256 of the running binary's file.</param>
    /// <param name="rid">The runtime identifier, for example <c>linux-x64</c>.</param>
    /// <returns>The comparison.</returns>
    public static VersionPinResult Check(string pinJson, string runningVersion, string runningSha256, string rid)
    {
        ArgumentNullException.ThrowIfNull(runningVersion);
        ArgumentNullException.ThrowIfNull(runningSha256);
        try
        {
            using var document = JsonDocument.Parse(pinJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("sha256", out var hashes) || hashes.ValueKind != JsonValueKind.Object)
            {
                return new VersionPinResult(VersionPinStatus.Invalid, "zyggy.json is not { version, sha256 }");
            }

            var pinned = version.GetString()!;
            var running = runningVersion.Split('+')[0];
            if (!string.Equals(pinned, running, StringComparison.Ordinal))
            {
                return new VersionPinResult(VersionPinStatus.Mismatch, $"version {running} is not the pinned {pinned}");
            }

            if (!hashes.TryGetProperty(rid, out var hash) || hash.ValueKind != JsonValueKind.String)
            {
                return new VersionPinResult(VersionPinStatus.Mismatch, $"zyggy.json pins no sha256 for {rid}");
            }

            return string.Equals(hash.GetString(), runningSha256, StringComparison.OrdinalIgnoreCase)
                ? new VersionPinResult(VersionPinStatus.Match, null)
                : new VersionPinResult(VersionPinStatus.Mismatch, $"sha256 of the binary is not the pinned one for {rid}");
        }
        catch (JsonException)
        {
            return new VersionPinResult(VersionPinStatus.Invalid, "zyggy.json is not valid JSON");
        }
    }
}
