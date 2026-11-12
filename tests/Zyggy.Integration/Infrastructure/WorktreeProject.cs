namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A project for worktree runs (spec 16 slice 3): under <c>&lt;temp&gt;/zyggy-it/&lt;guid&gt;/</c> a bare <c>origin.git</c> and its clone
/// <c>shop/</c> on <c>main</c> with <c>.claude/</c>, <c>README.md</c> and <c>src/a.txt</c> in one pushed commit and <c>origin/HEAD</c> set;
/// <c>node/</c> beside them is the node directory, so the worktree folder lives under the same parent as the project. Git runs through
/// the bus fixture's isolated git; the fixture's own identity is local config of the clone, never global. Deleted on dispose.
/// </summary>
public sealed class WorktreeProject : IDisposable
{
    private readonly BusRepoFixture _git;

    private WorktreeProject(BusRepoFixture git, string root)
    {
        _git = git;
        Root = root;
    }

    /// <summary>Gets the parent of the project, the origin and the node directory (the discovery scan's <c>devRoots</c> entry).</summary>
    public string Root { get; }

    /// <summary>Gets the project's absolute path, the job's <c>project</c> field.</summary>
    public string Path => System.IO.Path.Combine(Root, "shop");

    /// <summary>Gets the bare repository the project pushes to.</summary>
    public string OriginDir => System.IO.Path.Combine(Root, "origin.git");

    /// <summary>Gets the node directory; worktrees go to <c>&lt;NodeDir&gt;/worktrees/&lt;tenant&gt;/&lt;id&gt;</c>.</summary>
    public string NodeDir => System.IO.Path.Combine(Root, "node");

    public static async Task<WorktreeProject> CreateAsync(BusRepoFixture git, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        var project = new WorktreeProject(git, System.IO.Path.Combine(ScratchProject.ParentRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(project.Root);
        await git.RunGitAsync(project.Root, ["init", "-q", "--bare", "--initial-branch=main", project.OriginDir], ct);
        await git.RunGitAsync(project.Root, ["clone", "-q", project.OriginDir, project.Path], ct);
        foreach (string[] config in (string[][])[["user.name", "alice (fixture)"], ["user.email", "alice@example.invalid"], ["commit.gpgsign", "false"], ["core.autocrlf", "false"]])
        {
            await project.GitAsync(["config", config[0], config[1]], ct);
        }

        await project.GitAsync(["symbolic-ref", "HEAD", "refs/heads/main"], ct);
        Directory.CreateDirectory(System.IO.Path.Combine(project.Path, ".claude"));
        Directory.CreateDirectory(System.IO.Path.Combine(project.Path, "src"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(project.Path, ".claude", "settings.json"), "{}\n", ct);
        await File.WriteAllTextAsync(System.IO.Path.Combine(project.Path, "README.md"), "# shop\n", ct);
        await File.WriteAllTextAsync(System.IO.Path.Combine(project.Path, "src", "a.txt"), "a\n", ct);
        await project.GitAsync(["add", "-A"], ct);
        await project.GitAsync(["commit", "-q", "-m", "initial"], ct);
        await project.GitAsync(["push", "-q", "origin", "main"], ct);
        await project.GitAsync(["remote", "set-head", "origin", "main"], ct);
        return project;
    }

    /// <summary>Runs git in the project; throws when git fails.</summary>
    public Task<string> GitAsync(IReadOnlyList<string> args, CancellationToken ct) => _git.RunGitAsync(Path, args, ct);

    /// <summary>Runs git in the bare origin; throws when git fails.</summary>
    public Task<string> OriginGitAsync(IReadOnlyList<string> args, CancellationToken ct) => _git.RunGitAsync(OriginDir, args, ct);

    /// <summary>The SHA <paramref name="reference"/> points at in <paramref name="repository"/>, or <see langword="null"/> when it does not exist.</summary>
    public async Task<string?> ResolveAsync(string repository, string reference, CancellationToken ct)
    {
        try
        {
            return await _git.RunGitAsync(repository, ["rev-parse", "--verify", "-q", reference], ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Adds a <c>pre-commit</c> hook to the project that refuses every commit.</summary>
    public Task AddFailingPreCommitHookAsync(CancellationToken ct) =>
        WriteHookAsync(System.IO.Path.Combine(Path, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho 'pre-commit: refused by fixture' >&2\nexit 1\n", ct);

    /// <summary>Adds a <c>pre-receive</c> hook to the origin that refuses every push.</summary>
    public Task AddRefusingOriginAsync(CancellationToken ct) =>
        WriteHookAsync(System.IO.Path.Combine(OriginDir, "hooks", "pre-receive"), "#!/bin/sh\necho 'pre-receive: refused by fixture' >&2\nexit 1\n", ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directories may accumulate under <temp>/zyggy-it/ — see the Integration README "Leaked temp directories".
        }
    }

    private static async Task WriteHookAsync(string path, string script, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, script, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
