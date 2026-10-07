using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>One document to parse: the input as given, the run directory (<c>ZYGGY_M365_RUN_DIR</c>) and the directory a relative input is resolved from.</summary>
internal sealed record ParseRequest(string Input, string? RunDirectory, string WorkingDirectory);

/// <summary>The exit code, the bytes for stdout and the stderr text (whole lines).</summary>
internal sealed record ParseResult(int Exit, byte[] Stdout, string Stderr);

/// <summary>
/// A downloaded document becomes bounded text and nothing else — the template's <c>parse.sh</c> (spec 33 AC-29). MarkItDown runs on one
/// file inside the run directory under <c>prlimit --as</c> (deviation 3) and a 120 s timeout; control characters are removed, every line
/// that matches a secret pattern is replaced by <c>[line withheld: matches secret pattern &lt;name&gt;]</c>, the text is cut at
/// <c>file_text_cap_bytes</c>. The input is deleted in every case once it is known to be inside the run directory (a symbolic link inside
/// is removed, never its target); a file outside is refused and left alone.
/// Exit 0 · 3 configuration · 5 refused · 6 MarkItDown failed or timed out.
/// </summary>
internal sealed partial class DocumentParser(
    IProcessRunner runner,
    Func<M365ConfigurationLoad> loadConfiguration,
    Func<string, string?> findProgram,
    Func<SecretPatternsLoad> loadSecretPatterns)
{
    private const string Prefix = "parse: ";
    private const string MemoryLimit = "--as=2147483648";
    private const int ErrorCharacters = 200;
    private const int HeadMargin = 4096;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static readonly HashSet<string> Types = new(["docx", "xlsx", "pptx", "pdf", "txt", "md", "csv", "json", "html", "htm"], StringComparer.Ordinal);

    public async Task<ParseResult> ParseAsync(ParseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stderr = new StringBuilder();

        // 1. The run directory and containment.
        if (string.IsNullOrEmpty(request.RunDirectory))
        {
            return Done(3, stderr, "configuration error: ZYGGY_M365_RUN_DIR is not set");
        }

        if (!Directory.Exists(request.RunDirectory))
        {
            return Done(3, stderr, $"configuration error: ZYGGY_M365_RUN_DIR {request.RunDirectory} is not a directory");
        }

        var runLexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RunDirectory, request.WorkingDirectory));
        var runReal = PathResolution.RealPath(runLexical);
        var pathLexical = Path.GetFullPath(request.Input, request.WorkingDirectory);
        if (!IsUnder(pathLexical, runLexical))
        {
            return Done(5, stderr, "refused: not in the run directory");
        }

        if (new FileInfo(pathLexical).LinkTarget is not null)
        {
            File.Delete(pathLexical);
            return Done(5, stderr, "refused: not in the run directory");
        }

        var pathReal = PathResolution.RealPath(pathLexical);
        if (!IsUnder(pathReal, runReal))
        {
            return Done(5, stderr, "refused: not in the run directory");
        }

        // From here the input is ours to delete, whatever happens.
        try
        {
            return await ParseInsideAsync(pathReal, request.WorkingDirectory, stderr, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(pathReal);
        }
    }

    private async Task<ParseResult> ParseInsideAsync(string path, string workingDirectory, StringBuilder stderr, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            return Done(5, stderr, $"refused: {name} is not a regular file");
        }

        // 2. Configuration and tools.
        var configuration = loadConfiguration();
        if (configuration.Warning is not null)
        {
            stderr.Append(configuration.Warning).Append('\n');
        }

        if (configuration.Configuration is not { } config)
        {
            return Done(3, stderr, configuration.Error!);
        }

        if (findProgram("markitdown") is not { } markitdown)
        {
            return Done(3, stderr, "markitdown not found — runbook 13 \"MarkItDown\"");
        }

        if (findProgram("prlimit") is not { } prlimit)
        {
            return Done(3, stderr, "prlimit not found");
        }

        var load = loadSecretPatterns();
        if (load.Patterns is not { } patterns)
        {
            return Done(3, stderr, "configuration error: " + load.Error);
        }

        // 3. Size and type.
        var size = new FileInfo(path).Length;
        if (size > config.FileMaxBytes)
        {
            return Done(5, stderr, $"refused: {name} is {size} bytes (limit {config.FileMaxBytes})");
        }

        var extension = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : string.Empty;
        if (extension.Length == 0 || !Types.Contains(extension.ToLowerInvariant()))
        {
            return Done(5, stderr, $"refused: type {(extension.Length == 0 ? "(none)" : "." + extension)} not parsable");
        }

        // 4. MarkItDown, bounded.
        var cap = config.FileTextCapBytes;
        var result = await runner.RunAsync(
            new ProcessSpec(prlimit, [MemoryLimit, "--", markitdown, path], workingDirectory)
            {
                Timeout = Timeout,
                MaxStdoutBytes = (int)Math.Min(int.MaxValue, cap + HeadMargin + 65536),
            },
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Done(6, stderr, $"markitdown timed out after {Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }

        if (result.StartFailed || result.ExitCode != 0)
        {
            var first = FactValidator.Clean(result.Stderr.Split('\n')[0]);
            if (patterns.TryMatch(first, out var secret))
            {
                first = "first error line withheld: matches secret pattern " + secret;
            }

            return Done(6, stderr, $"markitdown failed ({CharacterPrefix(first, ErrorCharacters)})");
        }

        // 5. The text: control characters out, secret-shaped lines withheld, cut at the cap.
        return Text(Encoding.UTF8.GetBytes(result.Stdout), cap, patterns, name, stderr);
    }

    private static ParseResult Text(byte[] raw, long cap, SecretPatterns patterns, string name, StringBuilder stderr)
    {
        // head -c <cap + 4096> | tr -d '\000-\010\013\014\016-\037\177' | tr -d '\r'
        var head = raw.AsSpan(0, (int)Math.Min(raw.Length, cap + HeadMargin));
        var clean = new List<byte>(head.Length);
        foreach (var b in head)
        {
            if (b is (>= 0x00 and <= 0x08) or 0x0b or 0x0c or (>= 0x0e and <= 0x1f) or 0x7f or 0x0d)
            {
                continue;
            }

            clean.Add(b);
        }

        // mapfile -t: lines split on LF, a last line without one included; each printed back with LF.
        // Spec 35 AC-30: a line where only number-shaped patterns match keeps its text with the number redacted (re-tested; else withheld).
        var output = new List<byte>(clean.Count + 64);
        var redactedEnds = new List<int>();
        foreach (var line in Lines([.. clean]))
        {
            var text = Encoding.UTF8.GetString(line);
            if (patterns.TryMatch(text, out var secret))
            {
                if (patterns.TryRedactNumberShaped(text, out var redacted, out _))
                {
                    output.AddRange(Encoding.UTF8.GetBytes(redacted));
                    redactedEnds.Add(output.Count);
                }
                else
                {
                    output.AddRange(Encoding.UTF8.GetBytes($"[line withheld: matches secret pattern {secret}]"));
                }
            }
            else
            {
                output.AddRange(line);
            }

            output.Add((byte)'\n');
        }

        var note = string.Empty;
        byte[] final;
        if (output.Count > cap)
        {
            final = output.Take((int)cap).ToArray();
            if (final.Length > 0 && final[^1] != (byte)'\n')
            {
                final = [.. final, (byte)'\n'];
            }

            note = $", cut at {cap} bytes";
        }
        else
        {
            final = [.. output];
        }

        // Counted in what is printed: a line withheld beyond the cut is not shown, so not counted.
        var lines = Lines(final);
        var shown = lines.Count;
        var hidden = lines.Count(line => Withheld().IsMatch(Encoding.UTF8.GetString(line)));
        var redactedShown = redactedEnds.Count(end => end <= final.Length);
        var redactedNote = redactedShown > 0 ? $", {redactedShown} redacted" : string.Empty;
        byte[] stdout = note.Length == 0 ? final : [.. final, .. Encoding.UTF8.GetBytes($"[cut at {cap} bytes]\n")];
        stderr.Append(Prefix).Append(CultureInfo.InvariantCulture, $"{name} {shown - hidden} lines, {hidden} withheld{redactedNote}{note}\n");
        return new ParseResult(0, stdout, stderr.ToString());
    }

    private static List<byte[]> Lines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0)
            {
                lines.Add(bytes[start..]);
                break;
            }

            lines.Add(bytes[start..end]);
            start = end + 1;
        }

        return lines;
    }

    // ${first:0:200} under C.UTF-8: characters, not bytes.
    private static string CharacterPrefix(string text, int characters) =>
        string.Concat(text.EnumerateRunes().Take(characters).Select(r => r.ToString()));

    private static ParseResult Done(int exit, StringBuilder stderr, string message)
    {
        stderr.Append(Prefix).Append(message).Append('\n');
        return new ParseResult(exit, [], stderr.ToString());
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // rm -f: a path that cannot be removed is left as it is.
        }
    }

    [GeneratedRegex(@"\A\[line withheld: matches secret pattern [a-z0-9-]*\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex Withheld();
}
