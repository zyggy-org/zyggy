namespace Zyggy.Core.Secrets;

/// <summary>Options of the file secret store, the P0 store on every machine (founding spec §8, O20).</summary>
public sealed class FileSecretStoreOptions
{
    /// <summary>
    /// Gets or sets the root directory; keys live at <c>&lt;root&gt;/&lt;tenant&gt;/&lt;name&gt;</c>. When null, the default is
    /// <c>&lt;LocalApplicationData&gt;/zyggy/secrets</c> (<c>%LOCALAPPDATA%\zyggy\secrets</c> on Windows,
    /// <c>~/.local/share/zyggy/secrets</c> on Linux).
    /// </summary>
    public string? RootDirectory { get; set; }
}
