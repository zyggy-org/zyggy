using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zyggy.Core.Processes;

/// <summary>
/// Spec 61 slice 8: the id of the process that started a process, for <c>zyggy-node --exit-with-parent</c>. Linux: the fourth field of
/// <c>/proc/&lt;pid&gt;/stat</c> (an orphan reads 1 or its subreaper). Windows: the process's basic information
/// (<c>InheritedFromUniqueProcessId</c>), which keeps naming the parent's pid after the parent ended — <see cref="ParentWatch"/> tells the
/// two apart by start time. Another system, or a read that fails: <see langword="null"/>.
/// </summary>
internal static partial class ParentProcess
{
    /// <summary>The parent pid of <paramref name="pid"/>, or <see langword="null"/> when it cannot be read.</summary>
    public static int? Of(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return Linux(pid);
        }

        return OperatingSystem.IsWindows() ? Windows.Of(pid) : null;
    }

    private static int? Linux(int pid)
    {
        string text;
        try
        {
            text = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ParentOfStat(text);
    }

    /// <summary>The parent pid in the text of a Linux <c>/proc/&lt;pid&gt;/stat</c> file, or <see langword="null"/>.</summary>
    internal static int? ParentOfStat(string text)
    {
        // "<pid> (<comm>) <state> <ppid> …"; comm may hold spaces and parentheses, so read after the last ')'.
        int close = text.LastIndexOf(')');
        string[] fields = close < 0 ? [] : text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) ? parent : null;
    }

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessBasicInformation = 0;

        public static int? Of(int pid)
        {
            nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == 0)
            {
                return null;
            }

            try
            {
                var info = default(BasicInformation);
                int status = NtQueryInformationProcess(process, ProcessBasicInformation, ref info, Marshal.SizeOf<BasicInformation>(), out _);
                return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInformation
        {
            public nint ExitStatus;
            public nint PebBaseAddress;
            public nint AffinityMask;
            public nint BasePriority;
            public nint UniqueProcessId;
            public nint InheritedFromUniqueProcessId;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        // The process's basic information (its parent pid): a process query, never a credential call (spec 09).
        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(nint process, int informationClass, ref BasicInformation information, int length, out int returned);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
