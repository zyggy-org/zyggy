using Zyggy.Core.Dream;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The dream's version rule for the m365 model-run verbs (spec 33 AC-34): with <c>ZYGGY_INSTANCE_DIR</c> set, the running binary's version
/// and SHA-256 must match <c>instance/zyggy.json</c> before any request or model run.
/// </summary>
internal static class BinaryPin
{
    /// <summary>The configuration error, or <see langword="null"/> when there is no instance or the pin matches.</summary>
    public static string? Check(string? instanceDirectory, string version, string sha256, string rid, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        if (instanceDirectory is null)
        {
            return null;
        }

        var pinPath = Path.Join(instanceDirectory, "zyggy.json");
        if (readFile(pinPath) is not { } pin)
        {
            return $"configuration error: version_mismatch: {pinPath} is missing (the version pin)";
        }

        var check = VersionPin.Check(pin, version, sha256, rid);
        return check.Status == VersionPinStatus.Match ? null : "configuration error: version_mismatch: " + check.Detail;
    }
}
