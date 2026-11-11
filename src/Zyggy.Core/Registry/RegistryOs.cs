namespace Zyggy.Core.Registry;

/// <summary>The operating system a registry file names (founding spec §4 "Registry file"); written lowercase.</summary>
public enum RegistryOs
{
    /// <summary>Windows.</summary>
    Windows,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>macOS.</summary>
    Macos,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>Helpers for <see cref="RegistryOs"/>.</summary>
public static class RegistryOsExtensions
{
    /// <summary>Gets the operating system this process runs on.</summary>
    /// <returns>The current operating system.</returns>
    public static RegistryOs Current() =>
        OperatingSystem.IsWindows() ? RegistryOs.Windows
        : OperatingSystem.IsLinux() ? RegistryOs.Linux
        : OperatingSystem.IsMacOS() ? RegistryOs.Macos
        : RegistryOs.Other;

    /// <summary>Gets the wire text of an operating system (<c>windows</c>, <c>linux</c>, <c>macos</c>, <c>other</c>).</summary>
    /// <param name="os">The operating system.</param>
    /// <returns>The lowercase wire text.</returns>
    public static string ToWire(this RegistryOs os) => os switch
    {
        RegistryOs.Windows => "windows",
        RegistryOs.Linux => "linux",
        RegistryOs.Macos => "macos",
        RegistryOs.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(os), os, "unknown operating system"),
    };

    internal static bool TryParseWire(string text, out RegistryOs os)
    {
        (bool ok, os) = text switch
        {
            "windows" => (true, RegistryOs.Windows),
            "linux" => (true, RegistryOs.Linux),
            "macos" => (true, RegistryOs.Macos),
            "other" => (true, RegistryOs.Other),
            _ => (false, RegistryOs.Other),
        };
        return ok;
    }
}
