using System.Text.Json;
using System.Text.Json.Serialization;

using Zyggy.Core.M365;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The pending sign-in <c>pending-auth.json</c> (spec 36 Files): the one-time <c>state</c> of the link <c>auth start</c> printed and when
/// it was made; 0600 in the 0700 state directory, deleted by <c>auth finish</c>.
/// </summary>
internal sealed record OAuthPending(
    string State,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset Created)
{
    /// <summary>Reads the pending sign-in; <see langword="null"/> when there is none or it is not readable.</summary>
    public static OAuthPending? Read(LinkedInPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            var pending = JsonSerializer.Deserialize(File.ReadAllBytes(paths.PendingAuth), LinkedInJsonContext.Default.OAuthPending);
            return pending is { State.Length: > 0 } ? pending : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the pending sign-in atomically (0600, the directory 0700), replacing an earlier one.</summary>
    /// <exception cref="StateDirectoryException">Thrown when the state directory cannot be created.</exception>
    public void Write(LinkedInPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        StateFiles.WriteAtomically(paths.StateDirectory, paths.PendingAuth, JsonSerializer.SerializeToUtf8Bytes(this, LinkedInJsonContext.Default.OAuthPending));
    }

    /// <summary>Deletes the pending sign-in (a used or refused state cannot be tried again).</summary>
    public static void Delete(LinkedInPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        File.Delete(paths.PendingAuth);
    }
}
