using System.Runtime.InteropServices;

namespace Zyggy.Core.Processes;

/// <summary>
/// The few libc calls .NET has no public API for (plan 33 Assumptions 5 and 6): a file's owner uid (<c>lstat</c>), the effective uid,
/// and <c>execve</c>. Linux only; the struct offsets are those of glibc on x86-64 and aarch64.
/// </summary>
internal static partial class UnixNative
{
    /// <summary>The owner uid of <paramref name="path"/> (the link itself, not its target), or <see langword="null"/>.</summary>
    public static uint? Owner(string path)
    {
        var buffer = new byte[256];
        if (LStat(path, buffer) != 0)
        {
            return null;
        }

        var offset = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => 28,
            Architecture.Arm64 => 24,
            _ => -1,
        };
        return offset < 0 ? null : BitConverter.ToUInt32(buffer, offset);
    }

    public static uint EffectiveUserId() => GetEffectiveUserId();

    /// <summary>
    /// Replaces this process with <paramref name="path"/> — argv[0] the path, then <paramref name="arguments"/>, the environment exactly
    /// <paramref name="environment"/>. Returns only on failure, with the errno text.
    /// </summary>
    public static string Exec(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var argv = Pointers([path, .. arguments]);
        var envp = Pointers([.. environment.Select(kv => kv.Key + "=" + kv.Value)]);
        try
        {
            _ = Execve(path, argv, envp);
            return Marshal.GetLastPInvokeErrorMessage();
        }
        finally
        {
            foreach (var pointer in argv.Concat(envp).Where(p => p != IntPtr.Zero))
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    // A NULL-terminated array of UTF-8 C strings.
    private static IntPtr[] Pointers(string[] values) => [.. values.Select(Marshal.StringToCoTaskMemUTF8), IntPtr.Zero];

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LStat(string path, byte[] buffer);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "execve", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Execve(string path, IntPtr[] argv, IntPtr[] envp);
}

/// <summary>Replaces the current process (the one process start outside <see cref="IProcessRunner"/>, spec 33 Contracts).</summary>
internal interface IProcessReplacer
{
    /// <summary>Returns only on failure, with the reason.</summary>
    string Exec(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment);
}

/// <summary><c>execve(2)</c>: the server keeps this process id, so systemd's SIGTERM reaches it and its exit code is the unit's.</summary>
internal sealed class PosixProcessReplacer : IProcessReplacer
{
    public string Exec(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
        OperatingSystem.IsWindows() ? "not supported on this platform" : UnixNative.Exec(path, arguments, environment);
}
