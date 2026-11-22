using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Microsoft.Win32.SafeHandles;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The full path a tool call names, as the file system will open it (verify 01M81XMXM722ESMJ8M0Y6VBP6N, spec 58 slice 7): the outlook
/// guard compares that with the state folder, so another spelling of the pass file or the approval key — <c>..</c> and <c>.</c>, a
/// <c>\\?\</c> or <c>\\.\</c> device path, an admin share (<c>\\host\c$\…</c>), an 8.3 short name, a junction or a symbolic link, an
/// environment variable or <c>~</c> — leads to the same place.
/// </summary>
/// <remarks>
/// The part of the path that exists is resolved by the file system itself (Windows: the final path of an open handle; elsewhere: each
/// link followed); the rest is appended as written. A path the guard cannot resolve stays as written (the text checks still see it).
/// Hard links and shares set up to point at the folder are not followed: creating one needs a command the owner is prompted for (Q24).
/// </remarks>
internal static partial class OutlookPaths
{
    /// <summary>Resolves <paramref name="path"/> against the session's directory.</summary>
    /// <param name="path">The path as the call names it.</param>
    /// <param name="cwd">The session's directory (relative paths start there).</param>
    /// <param name="environment">The variables a path may name (<c>$X</c>, <c>${X}</c>, <c>$env:X</c>, <c>%X%</c>) and the home folder for <c>~</c>.</param>
    /// <returns>The full path, or <paramref name="path"/> when it cannot be resolved.</returns>
    public static string Resolve(string path, string cwd, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(cwd);
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            string full = Path.GetFullPath(Lexical(Expand(path.Trim(), environment)), cwd);
            return Physical(full);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (both full paths).</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        string p = Comparable(path), f = Comparable(folder);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return f.Length > 0 && (string.Equals(p, f, comparison) || p.StartsWith(f + "/", comparison));
    }

    private static string Comparable(string path) => path.Replace('\\', '/').TrimEnd('/');

    // ~ and environment variables, from the session's environment; an unknown name stays as written.
    private static string Expand(string path, IReadOnlyDictionary<string, string?> environment)
    {
        string? home = Variable(environment, "USERPROFILE") ?? Variable(environment, "HOME");
        if (home is not null && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            path = home + path[1..];
        }

        return VariableReference().Replace(path, m =>
        {
            string name = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["c"].Success ? m.Groups["c"].Value : m.Groups["d"].Value;
            return Variable(environment, name) ?? m.Value;
        });
    }

    private static string? Variable(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value)
            ? value
            : OperatingSystem.IsWindows() ? environment.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value)).Value : null;

    // Windows spellings that GetFullPath keeps as they are: device paths, and an admin share of a local drive (any host: it refuses
    // more than it must).
    private static string Lexical(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        return AdminShare().Match(p) is { Success: true } share ? share.Groups["drive"].Value + @":\" + share.Groups["rest"].Value : p;
    }

    // The existing part resolved by the file system, the rest appended as written.
    private static string Physical(string full)
    {
        string? existing = full;
        var rest = new Stack<string>();
        while (existing is not null && !File.Exists(existing) && !Directory.Exists(existing))
        {
            rest.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing);
        }

        if (existing is null)
        {
            return full;
        }

        string resolved = (OperatingSystem.IsWindows() ? FinalPath(existing) : null) ?? FollowLinks(existing);
        return rest.Aggregate(resolved, Path.Join);
    }

    // Each component that is a link replaced by its final target.
    private static string FollowLinks(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        string current = root;
        foreach (string part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    // The path Windows opens (8.3 names expanded, junctions and symbolic links followed), or null.
    [SupportedOSPlatform("windows")]
    private static string? FinalPath(string path)
    {
        const uint ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[1024];
        uint length = FinalPathName(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        string final = new(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint FinalPathName(SafeFileHandle file, [Out] char[] buffer, uint length, uint flags);

    [GeneratedRegex(@"%(?<a>\w+)%|\$env:(?<b>\w+)|\$\{(?<c>\w+)\}|\$(?<d>\w+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VariableReference();

    [GeneratedRegex(@"\A\\\\[^\\]+\\(?<drive>[A-Za-z])\$(?:\\(?<rest>.*))?\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AdminShare();
}
