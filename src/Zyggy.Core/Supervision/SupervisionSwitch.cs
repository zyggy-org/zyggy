using System.Globalization;

namespace Zyggy.Core.Supervision;

/// <summary>
/// The owner's stop switch (deliverable 18, "Stop"): a marker file in the node's own folder. While it is there, Central's node starts
/// no supervisor run, and a laptop's node rejects every job that carries a mandate (<c>mandate_invalid</c>). <c>zyggy supervise stop</c>
/// writes it, <c>zyggy supervise start</c> removes it. A file and nothing else, so it holds without the bus and across restarts.
/// </summary>
public static class SupervisionSwitch
{
    /// <summary>The marker's file name in the node directory.</summary>
    public const string FileName = "supervision-stopped";

    /// <summary>The marker's path in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory (<c>nodeDir</c> in <c>node.json</c>, or its per-OS default).</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeDir"/> is null or empty.</exception>
    public static string MarkerPath(string nodeDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        return Path.Join(nodeDir, FileName);
    }

    /// <summary>Whether the owner stopped supervision on this machine.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <returns><see langword="true"/> while the marker exists.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeDir"/> is null or empty.</exception>
    public static bool IsStopped(string nodeDir) => File.Exists(MarkerPath(nodeDir));

    /// <summary>Writes the marker, with the time it was written as its one line.</summary>
    /// <param name="nodeDir">The node directory; created when it is missing.</param>
    /// <param name="at">When the owner stopped.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeDir"/> is null or empty.</exception>
    /// <exception cref="IOException">Thrown when the file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the folder may not be written.</exception>
    public static void Stop(string nodeDir, DateTimeOffset at)
    {
        string path = MarkerPath(nodeDir);
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(path, at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\n");
    }

    /// <summary>Removes the marker.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <returns><see langword="true"/> when a marker was removed; <see langword="false"/> when there was none.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeDir"/> is null or empty.</exception>
    /// <exception cref="IOException">Thrown when the file cannot be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the folder may not be written.</exception>
    public static bool Start(string nodeDir)
    {
        string path = MarkerPath(nodeDir);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
