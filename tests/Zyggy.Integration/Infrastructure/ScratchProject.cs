namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A scratch project for the job runner's round trips (plan 06 Step 9): a directory under &lt;temp&gt;/zyggy-it/ holding a real
/// repository (<c>git init</c> through the fixture's isolated git), <c>.claude/</c> unless built <see cref="WithoutClaudeAsync"/>,
/// and optionally <c>.claude/agents/&lt;name&gt;.md</c>. Deleted on dispose, read-only git objects included.
/// </summary>
public sealed class ScratchProject : IDisposable
{
    private ScratchProject(string path) => Path = path;

    /// <summary>The project's absolute path, the job's <c>project</c> field.</summary>
    public string Path { get; }

    /// <summary>Creates a repository with <c>.claude/</c> and, when <paramref name="agent"/> is set, that subagent's file.</summary>
    public static Task<ScratchProject> CreateAsync(BusRepoFixture bus, CancellationToken ct, string? agent = null) =>
        BuildAsync(bus, claude: true, agent, ct);

    /// <summary>The parent of every default scratch project, <c>&lt;temp&gt;/zyggy-it</c> (plan 14 A10: listed in <c>devRoots</c> by the compositions that only resolve paths).</summary>
    public static string ParentRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zyggy-it");

    /// <summary>
    /// Creates a named project under <paramref name="parent"/> for discovery (plan 14 Step 4): a repository on <c>main</c> with <c>.claude/</c>,
    /// optionally an agent with a <c>description</c>, a skill and a <c>CLAUDE.md</c>.
    /// </summary>
    public static async Task<ScratchProject> CreateInAsync(
        BusRepoFixture bus,
        string parent,
        string name,
        CancellationToken ct,
        string? agent = null,
        string? agentDescription = null,
        string? skill = null,
        string? claudeMd = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var project = new ScratchProject(System.IO.Path.Combine(parent, name));
        Directory.CreateDirectory(System.IO.Path.Combine(project.Path, ".claude"));
        await bus.RunGitAsync(project.Path, ["init", "-q"], ct);
        await bus.RunGitAsync(project.Path, ["symbolic-ref", "HEAD", "refs/heads/main"], ct);
        if (agent is not null)
        {
            string agents = System.IO.Path.Combine(project.Path, ".claude", "agents");
            Directory.CreateDirectory(agents);
            string description = agentDescription is null ? string.Empty : "description: " + agentDescription + "\n";
            await File.WriteAllTextAsync(System.IO.Path.Combine(agents, agent + ".md"), "---\nname: " + agent + "\n" + description + "---\nAn agent.\n", ct);
        }

        if (skill is not null)
        {
            string skills = System.IO.Path.Combine(project.Path, ".claude", "skills", skill);
            Directory.CreateDirectory(skills);
            await File.WriteAllTextAsync(System.IO.Path.Combine(skills, "SKILL.md"), "---\nname: " + skill + "\ndescription: The " + skill + " skill\n---\n", ct);
        }

        if (claudeMd is not null)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(project.Path, "CLAUDE.md"), claudeMd, ct);
        }

        return project;
    }

    /// <summary>Creates a repository without <c>.claude/</c> (the pre-flight's <c>unknown_project</c> case).</summary>
    public static Task<ScratchProject> WithoutClaudeAsync(BusRepoFixture bus, CancellationToken ct) =>
        BuildAsync(bus, claude: false, agent: null, ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leaked temp directories are documented in the README.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private static async Task<ScratchProject> BuildAsync(BusRepoFixture bus, bool claude, string? agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var project = new ScratchProject(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(project.Path);
        await bus.RunGitAsync(project.Path, ["init", "-q"], ct);
        await File.WriteAllTextAsync(System.IO.Path.Combine(project.Path, "README.md"), "Scratch project for a job round trip.\n", ct);
        if (claude)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(project.Path, ".claude"));
        }

        if (agent is not null)
        {
            var agents = System.IO.Path.Combine(project.Path, ".claude", "agents");
            Directory.CreateDirectory(agents);
            await File.WriteAllTextAsync(System.IO.Path.Combine(agents, agent + ".md"), "---\nname: " + agent + "\n---\nAn agent.\n", ct);
        }

        return project;
    }
}
