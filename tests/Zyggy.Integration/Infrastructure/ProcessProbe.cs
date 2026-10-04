namespace Zyggy.Integration.Infrastructure;

/// <summary>Finds processes still running in a directory, to prove a killed process tree left no orphan.</summary>
public static class ProcessProbe
{
    /// <summary>
    /// Returns how many live processes have <paramref name="directory"/> as their working directory. On Linux this scans
    /// <c>/proc/*/cwd</c>; on Windows a live process with that working directory blocks deleting it, so the probe deletes
    /// the directory (retrying briefly) and reports 1 when it cannot.
    /// </summary>
    public static int ProcessesWithWorkingDirectory(string directory)
    {
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsLinux())
        {
            var count = 0;
            foreach (var proc in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(proc), out _))
                {
                    continue;
                }

                try
                {
                    var cwd = new FileInfo(Path.Combine(proc, "cwd")).LinkTarget;
                    if (cwd is not null && cwd.TrimEnd('/') == target)
                    {
                        count++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The process exited or belongs to another user.
                }
            }

            return count;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
                return 0;
            }
            catch (DirectoryNotFoundException)
            {
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        return 1;
    }
}
