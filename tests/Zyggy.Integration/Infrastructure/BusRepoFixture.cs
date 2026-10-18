using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A local bare git repository playing "origin" (GitHub), a working clone (the node's checkout, the system under test) and a
/// second clone acting as the sender (Central pushing jobs), isolated from the machine's git configuration. The tenant is a
/// required parameter: there is no default tenant anywhere (spec 04, the brief's rule). Seeding goes through the production
/// <see cref="BusPaths"/> so a layout change breaks the tests; assertions read the bare repository. Launches <c>git</c>
/// through <see cref="Process"/> directly by design: test infrastructure must not depend on the production git client.
/// </summary>
public class BusRepoFixture : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);

    private readonly string _globalConfigPath;

    private int _noise;

    public BusRepoFixture(TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Tenant = tenant;
        RootDir = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        BareDir = Path.Combine(RootDir, "bus.git");
        CloneDir = Path.Combine(RootDir, "bus");
        SenderDir = Path.Combine(RootDir, "sender");
        _globalConfigPath = Path.Combine(RootDir, "gitconfig");
    }

    /// <summary>The tenant this bus serves; every seeded path is under its prefix unless a seed names another tenant.</summary>
    public TenantId Tenant { get; }

    public string RootDir { get; }

    public string BareDir { get; }

    /// <summary>The node's checkout — the clone the code under test works in.</summary>
    public string CloneDir { get; }

    /// <summary>The sender's clone — jobs and foreign commits are pushed from here, never from <see cref="CloneDir"/>.</summary>
    public string SenderDir { get; }

    /// <summary>The environment that isolates git from the machine's configuration; the production <c>GitClient</c> gets it through its options in tests.</summary>
    public IReadOnlyDictionary<string, string?> GitEnvironment => new Dictionary<string, string?>
    {
        ["GIT_CONFIG_GLOBAL"] = _globalConfigPath,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    /// <summary>Removes the bare repository, so every remote operation fails (the "remote vanished" scenario).</summary>
    public void DeleteBare()
    {
        foreach (var file in Directory.EnumerateFiles(BareDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(BareDir, recursive: true);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RootDir);
        File.Create(_globalConfigPath).Dispose();

        var none = CancellationToken.None;
        await RunGitAsync(RootDir, ["init", "--bare", "--initial-branch=main", BareDir], none);
        await RunGitAsync(RootDir, ["clone", BareDir, CloneDir], none);
        await RunGitAsync(RootDir, ["clone", BareDir, SenderDir], none);
        foreach (string clone in new[] { CloneDir, SenderDir })
        {
            await RunGitAsync(clone, ["symbolic-ref", "HEAD", "refs/heads/main"], none);
            await RunGitAsync(clone, ["config", "user.name", clone == SenderDir ? "zyggy (sender)" : "zyggy (test)"], none);
            await RunGitAsync(clone, ["config", "user.email", clone == SenderDir ? "sender@zyggy.org" : "test@zyggy.org"], none);
            await RunGitAsync(clone, ["config", "commit.gpgsign", "false"], none);
            await RunGitAsync(clone, ["config", "core.autocrlf", "false"], none);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                // Git object files are read-only on Windows; Directory.Delete would throw on them.
                foreach (var file in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Directories may accumulate under <temp>/zyggy-it/ — see README "Leaked temp directories".
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs git with the fixture's isolated environment and returns trimmed stdout.</summary>
    /// <exception cref="InvalidOperationException">git exited non-zero (stderr in the message) or could not start.</exception>
    public async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
        Encoding.UTF8.GetString(await RunGitBytesAsync(workingDirectory, args, ct)).Trim();

    /// <summary>Runs git with the fixture's isolated environment and returns raw stdout bytes.</summary>
    /// <exception cref="InvalidOperationException">git exited non-zero (stderr in the message) or could not start.</exception>
    public async Task<byte[]> RunGitBytesAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = _globalConfigPath;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "git >= 2.32 must be on PATH to run the integration tests (tests/Zyggy.Integration/README.md, Prerequisites).",
                ex);
        }

        using var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"git {string.Join(' ', args)} exited with {process.ExitCode} in '{workingDirectory}':{Environment.NewLine}{stderr}"));
        }

        return stdout.ToArray();
    }

    public Task<string> HeadShaAsync(CancellationToken ct) =>
        RunGitAsync(BareDir, ["rev-parse", "main"], ct);

    public Task<string> ShowAsync(string path, CancellationToken ct) =>
        RunGitAsync(BareDir, ["show", $"main:{path}"], ct);

    /// <summary>The exact bytes of a file on the bare repository's <c>main</c>.</summary>
    public Task<byte[]> ShowBytesAsync(string path, CancellationToken ct) =>
        RunGitBytesAsync(BareDir, ["cat-file", "blob", $"main:{path}"], ct);

    /// <summary>Whether <paramref name="path"/> exists on the bare repository's <c>main</c>.</summary>
    public async Task<bool> ExistsOnMainAsync(string path, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(BareDir, ["cat-file", "-e", $"main:{path}"], ct);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Task<string> LastCommitSubjectAsync(CancellationToken ct) =>
        RunGitAsync(BareDir, ["log", "-1", "--format=%s", "main"], ct);

    /// <summary>Counts the commits of <paramref name="range"/> (for example <c>HEAD..origin/main</c>) in the node's clone.</summary>
    public async Task<int> RevListCountAsync(string range, CancellationToken ct) =>
        int.Parse(await RunGitAsync(CloneDir, ["rev-list", "--count", range], ct), CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates the §4 layout for <paramref name="tenant"/> on <c>main</c> through the production <see cref="BusPaths"/>:
    /// <c>PROTOCOL.md</c> (placeholder), the registry directory, every machine's <c>jobs</c>, <c>jobs/claimed</c>,
    /// <c>jobs/rejected</c>, <c>reports</c> and <c>context</c>, and <c>archive/2026-09</c>, each with a <c>.gitkeep</c>;
    /// committed as <c>seed &lt;tenant&gt;</c> from the sender and pulled into the node's clone.
    /// </summary>
    public async Task SeedTenantAsync(TenantId tenant, IReadOnlyList<MachineName> machines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(machines);
        await SyncSenderAsync(ct);

        string protocol = BusPaths.Resolve(SenderDir, BusPaths.Protocol);
        if (!File.Exists(protocol))
        {
            await File.WriteAllTextAsync(protocol, "# Zyggy bus protocol (placeholder; the text is deliverable 15)\n", ct);
        }

        var folders = new List<string> { BusPaths.Archive(tenant, 2026, 9) };
        foreach (MachineName machine in machines)
        {
            folders.Add(Path.GetDirectoryName(BusPaths.Registry(tenant, machine))!.Replace('\\', '/'));
            folders.Add(BusPaths.Jobs(tenant, machine));
            folders.Add(BusPaths.Claimed(tenant, machine));
            folders.Add(BusPaths.Rejected(tenant, machine));
            folders.Add(BusPaths.Reports(tenant, machine));
            folders.Add(BusPaths.Context(tenant, machine));
        }

        foreach (string folder in folders.Distinct(StringComparer.Ordinal))
        {
            string keep = BusPaths.Resolve(SenderDir, folder + "/.gitkeep");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.Create(keep).Dispose();
        }

        await CommitAndPushFromSenderAsync("seed " + tenant.Value, ct);
        await RunGitAsync(CloneDir, ["pull", "--rebase", "origin", "main"], ct);
    }

    /// <summary>
    /// Builds a <c>probe</c> job from <paramref name="from"/> to <paramref name="to"/> (fixed <c>created</c> instant,
    /// <c>timeout_minutes: 2</c>), signs it with the production signer, optionally mutates the serialized bytes, writes it at
    /// <c>jobs/&lt;ULID&gt;.md</c> of <paramref name="to"/> (or <paramref name="fileNameOverride"/>) and pushes it from the sender
    /// with the §5 subject <c>&lt;tenant&gt; job &lt;id&gt; &lt;from&gt;→&lt;to&gt;</c>. <paramref name="fields"/> replaces the probe's job
    /// fields (plan 06 Step 9: a job naming a scratch project); null keeps them.
    /// </summary>
    public async Task<SeededJob> SeedSignedJobAsync(
        MachineName from,
        MachineName to,
        KeyId signWith,
        ISecretStore store,
        string body,
        Func<byte[], byte[]>? mutate = null,
        string? fileNameOverride = null,
        JobFields? fields = null,
        EnvelopePriority priority = EnvelopePriority.Normal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(signWith);
        ArgumentNullException.ThrowIfNull(body);

        var id = EnvelopeId.New(FixedInstant);
        var header = new EnvelopeHeader(signWith.Tenant, id, from, to, FixedInstant, priority);
        Model job = Model.CreateJob(header, fields ?? new JobFields("probe", TimeoutMinutes: 2), Encoding.UTF8.GetBytes(body));
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(job, signWith, ct);
        if (!signed.IsAccepted)
        {
            throw new InvalidOperationException("Signing the seeded job failed: " + signed.Rejection);
        }

        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        bytes = mutate is null ? bytes : mutate(bytes);
        string relativePath = fileNameOverride ?? BusPaths.EnvelopeFile(BusPaths.Jobs(signWith.Tenant, to), id);
        await SeedRawFileAsync(relativePath, bytes, string.Create(CultureInfo.InvariantCulture, $"{signWith.Tenant.Value} job {id.Value} {from.Value}→{to.Value}"), ct);
        return new SeededJob(id, relativePath, bytes);
    }

    /// <summary>Writes <paramref name="bytes"/> at <paramref name="relativePath"/> and pushes it from the sender with <paramref name="subject"/>.</summary>
    public async Task SeedRawFileAsync(string relativePath, byte[] bytes, string subject, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(bytes);
        await SyncSenderAsync(ct);
        string full = BusPaths.Resolve(SenderDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);
        await CommitAndPushFromSenderAsync(subject, ct);
    }

    /// <summary>Pushes one foreign commit (a line appended to <c>PROTOCOL.md</c>) so the node's next push is rejected until it rebases.</summary>
    public async Task AdvanceMainFromSenderAsync(CancellationToken ct)
    {
        await SyncSenderAsync(ct);
        _noise++;
        string protocol = BusPaths.Resolve(SenderDir, BusPaths.Protocol);
        await File.AppendAllTextAsync(protocol, string.Create(CultureInfo.InvariantCulture, $"noise {_noise}\n"), ct);
        await CommitAndPushFromSenderAsync(string.Create(CultureInfo.InvariantCulture, $"noise {_noise}"), ct);
    }

    private async Task SyncSenderAsync(CancellationToken ct)
    {
        await RunGitAsync(SenderDir, ["fetch", "origin"], ct);
        bool originHasMain;
        try
        {
            await RunGitAsync(SenderDir, ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/main"], ct);
            originHasMain = true;
        }
        catch (InvalidOperationException)
        {
            originHasMain = false;
        }

        if (originHasMain)
        {
            await RunGitAsync(SenderDir, ["rebase", "origin/main"], ct);
        }
    }

    private async Task CommitAndPushFromSenderAsync(string subject, CancellationToken ct)
    {
        await RunGitAsync(SenderDir, ["add", "--all"], ct);
        await RunGitAsync(SenderDir, ["commit", "-q", "-m", subject], ct);
        await RunGitAsync(SenderDir, ["push", "-q", "origin", "HEAD:main"], ct);
    }
}

/// <summary>A job the fixture pushed to the bus: its id, its repository-relative path and the exact bytes on <c>main</c>.</summary>
public sealed record SeededJob(EnvelopeId Id, string RelativePath, byte[] Bytes);

/// <summary>The bus fixture for tenant <c>acme</c>, the one tenant every test uses; parameterless for <c>IClassFixture&lt;&gt;</c>.</summary>
public sealed class AcmeBusRepoFixture : BusRepoFixture
{
    public AcmeBusRepoFixture()
        : base(TenantId.Parse("acme"))
    {
    }
}
