namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A scratch project for the job runner (plan 06 "Shared rules"): a temp directory with an empty <c>.git/</c> (git is recorded, not
/// run) and <c>.claude/</c>, plus a marker file whose name must never reach a log line. Deleted on dispose.
/// </summary>
public sealed class ScratchProject : IDisposable
{
    public const string MarkerFileName = "marker-do-not-log-7f3a.txt";

    private ScratchProject(bool git, bool claude)
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, MarkerFileName), "contents\n");
        if (git)
        {
            Directory.CreateDirectory(Path.Combine(Root, ".git"));
        }

        if (claude)
        {
            Directory.CreateDirectory(Path.Combine(Root, ".claude"));
        }
    }

    public string Root { get; }

    public string LockPath => Path.Combine(Root, ".claude", "zyggy.lock");

    public static ScratchProject Create() => new(git: true, claude: true);

    public static ScratchProject WithoutGit() => new(git: false, claude: true);

    public static ScratchProject WithoutClaude() => new(git: true, claude: false);

    /// <summary>Writes <c>.claude/agents/&lt;name&gt;.md</c>.</summary>
    public ScratchProject WithAgent(string name)
    {
        var agents = Path.Combine(Root, ".claude", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, name + ".md"), "---\nname: " + name + "\n---\nAn agent.\n");
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lock handle of a failed test may still be open.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
