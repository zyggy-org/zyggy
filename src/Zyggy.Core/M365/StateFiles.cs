namespace Zyggy.Core.M365;

/// <summary>
/// The m365 state directory's file rules (<c>zy_m365_state_dir</c>, <c>write_atomic</c>): the directory created under umask 077 and kept
/// 0700, every file written 0600.
/// </summary>
internal static class StateFiles
{
    private const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates the directory (each new level 0700) and sets it to 0700.</summary>
    /// <exception cref="StateDirectoryException">Thrown when it cannot be created.</exception>
    public static void EnsureDirectory(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
                return;
            }

            CreateOwnerOnly(directory);
            File.SetUnixFileMode(directory, Owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StateDirectoryException(directory, ex);
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to a file created 0600 (replacing it).</summary>
    public static void WriteOwnerOnly(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(bytes);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerReadWrite);
        }
    }

    /// <summary><c>write_atomic</c>: a 0600 <c>&lt;file&gt;.tmp</c> beside the target, then a rename.</summary>
    /// <exception cref="StateDirectoryException">Thrown when the state directory cannot be created.</exception>
    public static void WriteAtomically(string stateDirectory, string path, byte[] bytes)
    {
        EnsureDirectory(stateDirectory);
        var temp = path + ".tmp";
        try
        {
            WriteOwnerOnly(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // mkdir -p under umask 077: every directory it creates is 0700.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CreateOwnerOnly(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent)
        {
            CreateOwnerOnly(parent);
        }

        Directory.CreateDirectory(directory, Owner);
    }
}
