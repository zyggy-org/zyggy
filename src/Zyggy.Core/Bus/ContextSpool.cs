using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>What <see cref="ContextSpool.Append"/> did.</summary>
public enum SpoolAppendResult
{
    /// <summary>The line was appended.</summary>
    Appended,

    /// <summary>The session's cap was reached: the marker was written instead of the line, once.</summary>
    CapReached,

    /// <summary>The marker was already there; nothing changed.</summary>
    AlreadyCapped,
}

/// <summary>One session's spool read back: its file, session, scope and body lines.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="SessionId">The session (the file name without its extension).</param>
/// <param name="Scope">The scope of its first line, <c>general</c> when the header is missing or malformed.</param>
/// <param name="Lines">The body lines, in order, without line endings.</param>
public sealed record SpoolFile(string Path, string SessionId, ContextScope Scope, IReadOnlyList<string> Lines);

/// <summary>
/// The per-session spool of a laptop's <c>Stop</c> hook (spec 12 D2): <c>&lt;nodeDir&gt;/context/&lt;tenant&gt;/&lt;session_id&gt;.md</c>, the
/// first line <c>scope: &lt;scope&gt;</c>, then one fact line per turn. Node state beside <c>runs/</c>, never a bus path and never memory;
/// written atomically, UTF-8 without BOM, LF.
/// </summary>
public sealed class ContextSpool
{
    private const string ScopePrefix = "scope: ";

    private const string Extension = ".md";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ContextEnvelopeOptions _options;

    /// <summary>Creates the spool of <paramref name="tenant"/> under <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node's state directory (<c>node.json</c> <c>nodeDir</c>).</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="clock">The clock (unused for now; the caller passes <c>now</c> to <see cref="Due"/>).</param>
    /// <param name="options">The cap and the marker.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public ContextSpool(string nodeDir, TenantId tenant, TimeProvider clock, ContextEnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory = Path.GetFullPath(DirectoryFor(nodeDir, tenant));
    }

    /// <summary>The spool directory.</summary>
    public string Directory { get; }

    /// <summary>The one place the spool directory is built: <c>&lt;nodeDir&gt;/context/&lt;tenant&gt;</c>.</summary>
    /// <param name="nodeDir">The node's state directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The directory path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string DirectoryFor(string nodeDir, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "context", tenant.Value);
    }

    /// <summary>Appends <paramref name="line"/> to the session's spool, creating it with its scope header; at the cap, writes the marker once.</summary>
    /// <param name="sessionId">The session (a safe file name).</param>
    /// <param name="scope">The scope written when the file is created.</param>
    /// <param name="line">The fact line, without line ending.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public SpoolAppendResult Append(string sessionId, ContextScope scope, string line)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(line);
        string path = PathOf(sessionId);
        string existing = File.Exists(path) ? File.ReadAllText(path, Utf8) : ScopePrefix + scope + "\n";
        string[] body = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        if (body.Contains(_options.CapMarker, StringComparer.Ordinal))
        {
            return SpoolAppendResult.AlreadyCapped;
        }

        bool capped = body.Length >= _options.MaxHookLinesPerSession;
        WriteAtomically(path, existing + (capped ? _options.CapMarker : line) + "\n");
        return capped ? SpoolAppendResult.CapReached : SpoolAppendResult.Appended;
    }

    /// <summary>
    /// The spools due for shipping: the named session's file whatever its age, then every other file last written more than
    /// <see cref="ContextEnvelopeOptions.StaleSpoolAge"/> before <paramref name="now"/>, by file name.
    /// </summary>
    /// <param name="sessionId">The ending session, or <see langword="null"/> for the stale spools only.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The due spools, read.</returns>
    public IReadOnlyList<SpoolFile> Due(string? sessionId, DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var due = new List<SpoolFile>();
        string? named = sessionId is null ? null : PathOf(sessionId);
        if (named is not null && File.Exists(named))
        {
            due.Add(Read(named));
        }

        DateTime threshold = now.UtcDateTime - _options.StaleSpoolAge;
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if (!string.Equals(file, named, StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < threshold)
            {
                due.Add(Read(file));
            }
        }

        return due;
    }

    /// <summary>Deletes a shipped spool; only a file of this spool directory is ever deleted.</summary>
    /// <param name="file">The spool.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the file is not in this spool directory (a caller bug).</exception>
    public void Delete(SpoolFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.Path)), Directory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the spool file is not in the spool directory");
        }

        File.Delete(file.Path);
    }

    private static SpoolFile Read(string path)
    {
        string[] lines = File.ReadAllText(path, Utf8).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ContextScope scope = new(ContextScopeKind.General, null);
        int start = 0;
        if (lines.Length > 0 && lines[0].StartsWith(ScopePrefix, StringComparison.Ordinal) && ContextScope.TryParse(lines[0][ScopePrefix.Length..], out ContextScope? parsed))
        {
            scope = parsed;
            start = 1;
        }

        return new SpoolFile(path, Path.GetFileNameWithoutExtension(path), scope, lines.Skip(start).ToArray());
    }

    private static void WriteAtomically(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".zyggy-tmp-" + Ulid.NewUlid();
        try
        {
            File.WriteAllBytes(temp, Utf8.GetBytes(content));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string PathOf(string sessionId) => Path.Join(Directory, sessionId + Extension);
}
