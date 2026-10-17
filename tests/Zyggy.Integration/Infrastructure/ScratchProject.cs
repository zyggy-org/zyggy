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
