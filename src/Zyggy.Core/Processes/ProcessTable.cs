using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Zyggy.Core.Processes;

/// <summary>What this machine says about one running process (spec 61 §4): its id, name, start time and whether it runs as this user.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="Name">The process name without extension (<c>zyggy-node</c>).</param>
/// <param name="StartedUtc">When the process started; <see langword="null"/> when the system does not say (another user's process on Windows).</param>
/// <param name="OwnedByCurrentUser">Whether the process runs as the user running this code.</param>
internal sealed record ProcessFacts(int Pid, string Name, DateTimeOffset? StartedUtc, bool OwnedByCurrentUser);

/// <summary>
/// The processes of this machine as <c>zyggy node stop</c> and <c>zyggy status</c> need them (spec 61 §4): find one by id, and end one
/// with its tree. Internal; the one implementation is <see cref="OsProcessTable"/>, tests substitute it.
/// </summary>
internal interface IProcessTable
{
    /// <summary>The current process.</summary>
    ProcessFacts Self();

    /// <summary>The process with <paramref name="pid"/>, or <see langword="null"/> when none runs.</summary>
    ProcessFacts? Find(int pid);

    /// <summary>Ends <paramref name="pid"/> and every process it started, and waits up to <paramref name="wait"/> for it to exit.</summary>
    /// <returns><see langword="null"/> when the process is gone; otherwise why it could not be ended.</returns>
    string? EndTree(int pid, TimeSpan wait);
}

/// <summary>The operating system's process table: <see cref="Process"/>, plus the owner check (<c>/proc/&lt;pid&gt;</c>'s uid on Linux, the token's user SID on Windows).</summary>
internal sealed partial class OsProcessTable : IProcessTable
{
    /// <summary>The one instance.</summary>
    public static OsProcessTable Instance { get; } = new();

    public ProcessFacts Self() => Find(Jobs.ProjectLock.CurrentProcessId) ?? new ProcessFacts(Jobs.ProjectLock.CurrentProcessId, "unknown", null, true);

    public ProcessFacts? Find(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // another user's process on Windows: HasExited needs a handle this user may not get
            }

            string name;
            try
            {
                name = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            DateTimeOffset? started;
            try
            {
                started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                started = null;
            }

            return new ProcessFacts(pid, name, started, OwnedByCurrentUser(pid));
        }
    }

    public string? EndTree(int pid, TimeSpan wait)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return null; // already exited
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or AggregateException)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                return process.WaitForExit(wait) ? null : "still running after " + ((int)wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s";
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or SystemException)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    private static bool OwnedByCurrentUser(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsOwner.IsCurrentUser(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            return UnixNative.Owner("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } uid && uid == UnixNative.EffectiveUserId();
        }

        return false;
    }

    /// <summary>The token user of a process compared with this process's user (Windows): a process this user cannot open is not this user's.</summary>
    [SupportedOSPlatform("windows")]
    private static partial class WindowsOwner
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;

        public static bool IsCurrentUser(int pid)
        {
            nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == 0)
            {
                return false;
            }

            try
            {
                if (!OpenProcessToken(process, TokenQuery, out nint token))
                {
                    return false;
                }

                try
                {
                    _ = GetTokenInformation(token, TokenUser, 0, 0, out int length);
                    if (length <= 0)
                    {
                        return false;
                    }

                    nint buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!GetTokenInformation(token, TokenUser, buffer, length, out _))
                        {
                            return false;
                        }

                        var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                        using WindowsIdentity me = WindowsIdentity.GetCurrent();
                        return me.User is { } user && sid.Equals(user);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    _ = CloseHandle(token);
                }
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        // The token calls through their documented API sets (Windows 8 and later): a token query, never a credential call (spec 09).
        [LibraryImport("api-ms-win-core-processthreads-l1-1-0.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenProcessToken(nint process, uint access, out nint token);

        [LibraryImport("api-ms-win-security-base-l1-1-0.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int returned);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
