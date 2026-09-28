using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Locates the compiled <c>fake-claude</c> next to the test assembly and decodes its capture file.
/// Stateless; the fake's contract is in <c>tools/fake-claude/README.md</c>.
/// </summary>
public static class FakeClaude
{
    /// <summary>
    /// Absolute path of the fake's apphost in the test output — the value a runner passes as <c>claude.path</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The apphost is absent (the ProjectReference to the fake is missing).</exception>
    public static string ExecutablePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException(
                    $"fake-claude not found at '{path}'. Zyggy.Integration.csproj must reference tools/fake-claude/FakeClaude.csproj.",
                    path);
        }
    }

    /// <summary>Directory holding the <c>*.jsonl</c> scenarios the fake streams.</summary>
    public static string ScenarioDirectory => Path.Combine(AppContext.BaseDirectory, "scenarios");

    /// <summary>
    /// Decodes a capture file: UTF-8 records each terminated by a single NUL byte; record 0 is the fake's
    /// working directory, the rest are its arguments in order.
    /// </summary>
    public static FakeClaudeCapture ReadCapture(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var records = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                records.Add(Encoding.UTF8.GetString(bytes, start, i - start));
                start = i + 1;
            }
        }

        return new FakeClaudeCapture(records[0], records.Skip(1).ToArray());
    }
}

/// <summary>What the fake saw: its working directory and its argument vector, bytes preserved.</summary>
public sealed record FakeClaudeCapture(string WorkingDirectory, IReadOnlyList<string> Arguments);
