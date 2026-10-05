using System.Globalization;
using System.Text;

namespace Zyggy.Core.M365;

/// <summary>The value grammar of a state key.</summary>
internal enum StateGrammar
{
    /// <summary>An ISO timestamp <c>YYYY-MM-DDTHH:MM:SSZ</c>.</summary>
    Iso,

    /// <summary>An ISO timestamp, optionally followed by <c>|&lt;item-id&gt;</c>.</summary>
    Cursor,

    /// <summary>One message id; values accumulate, each once.</summary>
    Id,
}

/// <summary>One named state key resolved to its file and grammar.</summary>
internal sealed record StateEntry(string Key, string FileName, StateGrammar Grammar);

/// <summary>The state directory could not be created (the shell's exit 3).</summary>
internal sealed class StateDirectoryException(string directory, Exception inner)
    : IOException($"state directory {directory} cannot be created", inner);

/// <summary>
/// The m365 connector's named state, as <c>state.sh</c> keeps it: files of 0600 in a 0700 directory, written as
/// <c>&lt;file&gt;.tmp</c> and renamed; the shell's files are read and appended to byte for byte.
/// </summary>
internal sealed class M365State(M365Paths paths, TimeProvider clock)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Returns the stored bytes as text, the last 24 hours for an absent <c>mail-watermark</c>, or <see langword="null"/>.</summary>
    public string? Get(StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = paths.StateFile(entry.FileName);
        if (File.Exists(path))
        {
            return Utf8NoBom.GetString(File.ReadAllBytes(path));
        }

        return entry.Key == "mail-watermark"
            ? clock.GetUtcNow().AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\n"
            : null;
    }

    /// <summary>Stores <paramref name="value"/>; an id is appended once.</summary>
    /// <exception cref="StateDirectoryException">Thrown when the state directory cannot be created.</exception>
    public void Set(StateEntry entry, string value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(value);
        var path = paths.StateFile(entry.FileName);
        byte[] content;
        if (entry.Grammar == StateGrammar.Id && File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (Utf8NoBom.GetString(existing).Split('\n').Contains(value, StringComparer.Ordinal))
            {
                return;
            }

            content = [.. existing, .. Utf8NoBom.GetBytes(value + "\n")];
        }
        else
        {
            content = Utf8NoBom.GetBytes(value + "\n");
        }

        WriteAtomically(path, content);
    }

    /// <summary>Removes the key's file, if any.</summary>
    public void Reset(StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        File.Delete(paths.StateFile(entry.FileName));
    }

    // zy_m365_state_dir + write_atomic: (umask 077 && mkdir -p), chmod 700, a 0600 <file>.tmp, mv -f.
    private void WriteAtomically(string path, byte[] content)
    {
        EnsureStateDirectory();
        var temp = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private void EnsureStateDirectory()
    {
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var directory = paths.StateDirectory;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
                return;
            }

            CreateOwnerOnly(directory, Owner);
            File.SetUnixFileMode(directory, Owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StateDirectoryException(directory, ex);
        }
    }

    // mkdir -p under umask 077: every directory it creates is 0700.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CreateOwnerOnly(string directory, UnixFileMode mode)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent)
        {
            CreateOwnerOnly(parent, mode);
        }

        Directory.CreateDirectory(directory, mode);
    }
}
