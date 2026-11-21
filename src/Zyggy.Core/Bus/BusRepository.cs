using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The bus layout and state moves for one node (founding spec §4 rows 1–3 and write rules, §5 "Git operations"): lists only
/// its own inbox, verifies before anything else, claims with one <c>git mv</c>, reports and rejects with signed envelopes, and
/// runs every change through the §5 write sequence — pull, change, one commit, push with the jittered retry rule, and a
/// deferred (local, unpushed, clean) commit when every push fails. A write outside the node's own folders is a caller bug.
/// </summary>
public sealed partial class BusRepository
{
    private const int MaxPushAttempts = 4;

    /// <summary>How many conflicting unpushed commits a write's first pull drops before it gives up and aborts the rebase.</summary>
    private const int MaxSkips = 16;

    private const string EnvelopeExtension = ".md";

    /// <summary>How a pull or write that never got the checkout lock begins its detail; <see cref="ItemSubmitter"/> maps it to the stage <c>lock</c>, and <c>zyggy sweep</c> to exit 4.</summary>
    public const string BusyDetail = "the bus checkout is busy: another zyggy process held its lock";

    /// <summary>How long a write or pull waits for another process's checkout lock (one full write sequence incl. its push retries fits).</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Deliverable 18: a machine's own status file older than this is removed with its next status write.</summary>
    private static readonly TimeSpan StatusMaxAge = TimeSpan.FromHours(24);

    private readonly GitClient _git;
    private readonly IBusProvider _provider;
    private readonly BusIdentity _me;
    private readonly string _checkoutRoot;
    private readonly EnvelopeSigner _signer;
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;
    private readonly ILogger<BusRepository> _log;

    internal BusRepository(
        GitClient git,
        IBusProvider provider,
        BusIdentity me,
        string checkoutRoot,
        EnvelopeSigner signer,
        TimeProvider clock,
        Func<double> jitter,
        ILogger<BusRepository> log)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _checkoutRoot = checkoutRoot;
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Gets this node's identity on the bus.</summary>
    public BusIdentity Me => _me;

    /// <summary>Gets the checkout's root folder (deliverable 18: a session task's claim is read from it; spec 59: the sender reads the target's registry file there).</summary>
    internal string CheckoutRoot => _checkoutRoot;

    /// <summary>Gets the clock this repository stamps its envelopes with (Central's reports on items, spec 41).</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>Whether this node's secret store holds a usable signing key for <paramref name="signWith"/> (the listener's start-up check).</summary>
    /// <param name="signWith">The key id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the key is present and long enough.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signWith"/> is null.</exception>
    public Task<bool> SignerCanSignAsync(KeyId signWith, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signWith);
        return _signer.CanSignAsync(signWith, ct);
    }

    /// <summary>The start-up check with its owner-facing sentence (spec 09 AC-6): usable, missing, too short, or the store cannot be opened.</summary>
    /// <param name="signWith">The key this machine signs with.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state and the sentence (empty when usable).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signWith"/> is null.</exception>
    public Task<SigningKeyCheck> CheckSigningKeyAsync(KeyId signWith, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signWith);
        return _signer.CheckKeyAsync(signWith, ct);
    }

    /// <summary>The checkout's current branch, or <see langword="null"/> when it is not a repository or HEAD is detached — a read that touches nothing (for a verb's pre-flight).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The branch name or null.</returns>
    public Task<string?> CheckoutBranchAsync(CancellationToken ct) =>
        Directory.Exists(_checkoutRoot) ? _git.CurrentBranchAsync(_checkoutRoot, ct) : Task.FromResult<string?>(null);

    /// <summary>How many local commits the remote branch lacks (deferred writes waiting for the next push, §5).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count; 0 when git cannot tell.</returns>
    public async Task<int> UnpushedCommitCountAsync(CancellationToken ct) =>
        (await _git.UnpushedCommitSubjectsAsync(_checkoutRoot, _provider.Remote.Branch, ct).ConfigureAwait(false)).Count;

    /// <summary>
    /// The §5 start-up rule: the checkout must be a repository on the bus branch with no rebase in progress and this tenant's
    /// prefix; a dirty tree is parked under <c>zyggy-&lt;machine&gt;-&lt;timestamp&gt;</c> with one warning, never reset.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was found.</returns>
    public async Task<BusStartupResult> EnsureCleanAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_checkoutRoot) || await _git.RevParseAsync(_checkoutRoot, "--git-dir", ct).ConfigureAwait(false) is null)
        {
            return BusStartupResult.NotARepository;
        }

        if (await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false))
        {
            return BusStartupResult.RebaseInProgress;
        }

        if (await _git.CurrentBranchAsync(_checkoutRoot, ct).ConfigureAwait(false) != _provider.Remote.Branch)
        {
            return BusStartupResult.WrongBranch;
        }

        if (!Directory.Exists(BusPaths.Resolve(_checkoutRoot, BusPaths.Tenant(_me.Tenant))))
        {
            return BusStartupResult.NoTenantDirectory;
        }

        IReadOnlyList<GitStatusEntry> status = await _git.StatusEntriesAsync(_checkoutRoot, ct).ConfigureAwait(false);
        if (status.Count == 0)
        {
            return BusStartupResult.Clean;
        }

        string stash = "zyggy-" + _me.Machine.Value + "-" + _clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        await _git.StashPushAsync(_checkoutRoot, stash, ct).ConfigureAwait(false);
        LogStashed(_me.Tenant.Value, _me.Machine.Value, status.Count, stash);
        return BusStartupResult.Stashed;
    }

    /// <summary>
    /// This node's unclaimed inbox: the top-level <c>*.md</c> files of <c>tenants/&lt;tenant&gt;/nodes/&lt;machine&gt;/jobs/</c> in
    /// ordinal order — never <c>claimed/</c>, <c>rejected/</c> or another tenant's or machine's folder (§4 write rules, §14).
    /// A file whose name is not a ULID is listed under a synthetic id so that intake rejects it as malformed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inbox files.</returns>
    public Task<IReadOnlyList<BusFile>> ListNewJobsAsync(CancellationToken ct) => ListFolderAsync(BusPaths.Jobs(_me.Tenant, _me.Machine), ct);

    /// <summary>
    /// This node's own claims: the <c>*.md</c> files of <c>tenants/&lt;tenant&gt;/nodes/&lt;machine&gt;/jobs/claimed/</c> in ordinal order
    /// (spec 07: resumed after a restart, counted for <c>jobsClaimedByMe</c>). Never another machine's folder.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The claimed files.</returns>
    public Task<IReadOnlyList<BusFile>> ListClaimedAsync(CancellationToken ct) => ListFolderAsync(BusPaths.Claimed(_me.Tenant, _me.Machine), ct);

    /// <summary>The checkout's <c>HEAD</c> commit (the <c>busHeadSha</c> health field); <see langword="null"/> when git cannot say.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full SHA, or <see langword="null"/>.</returns>
    public Task<string?> HeadShaAsync(CancellationToken ct) => _git.RevParseAsync(_checkoutRoot, "HEAD", ct);

    private Task<IReadOnlyList<BusFile>> ListFolderAsync(string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string full = BusPaths.Resolve(_checkoutRoot, folder);
        if (!Directory.Exists(full))
        {
            return Task.FromResult<IReadOnlyList<BusFile>>([]);
        }

        var files = new List<BusFile>();
        foreach (string name in Directory.EnumerateFiles(full, "*" + EnvelopeExtension, SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal))
        {
            EnvelopeId id = EnvelopeId.TryParse(Path.GetFileNameWithoutExtension(name), out EnvelopeId? parsed) ? parsed : EnvelopeId.New(_clock.GetUtcNow());
            files.Add(new BusFile(folder + "/" + name, id));
        }

        return Task.FromResult<IReadOnlyList<BusFile>>(files);
    }

    /// <summary>
    /// Verifies one inbox file before anything else (§4 "Signature"): 03's order (malformed → tenant → schema → type → key →
    /// fields → signature), then <c>to</c> must be this machine. A refused file carries its sender read leniently from the raw
    /// front matter so the rejection report can be addressed (ex-OQ10).
    /// </summary>
    /// <param name="file">An inbox file from <see cref="ListNewJobsAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is null.</exception>
    public async Task<JobIntake> VerifyIntakeAsync(BusFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        string stem = Path.GetFileNameWithoutExtension(file.RelativePath);
        if (!EnvelopeId.TryParse(stem, out EnvelopeId? named) || named != file.Id)
        {
            return new RejectedJob(file, EnvelopeRejectionReason.Malformed, null, "file name is not a ULID", null);
        }

        byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
        EnvelopeResult result = await _signer.ParseAndVerifyAsync(bytes, _me.Tenant, ct).ConfigureAwait(false);
        if (!result.IsAccepted)
        {
            EnvelopeRejection rejection = result.Rejection!;
            return new RejectedJob(file, rejection.Reason, rejection.Field, rejection.Detail, EnvelopeParser.PeekFrom(bytes));
        }

        Model envelope = result.Envelope!;
        if (envelope.Header.To != _me.Machine)
        {
            return new RejectedJob(file, EnvelopeRejectionReason.WrongRecipient, "to", "to is not this machine", envelope.Header.From)
            {
                Mandate = envelope.Job?.Mandate,
                Step = envelope.Job?.Step,
            };
        }

        if (envelope.Header.Id != file.Id)
        {
            LogIdDiffers(_me.Tenant.Value, _me.Machine.Value, file.Id.Value);
        }

        return new AcceptedJob(file, envelope);
    }

    /// <summary>
    /// Verifies one of this node's own <c>claimed/</c> files before it is resumed (spec 07, A7-5): the same checks as
    /// <see cref="VerifyIntakeAsync"/>. A <see cref="RejectedJob"/> here is <em>informational</em> — nothing is moved or reported;
    /// the file is left for the sweep.
    /// </summary>
    /// <param name="claimed">A file from <see cref="ListClaimedAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verdict; an <see cref="AcceptedJob"/> carries the claimed path as its file.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="claimed"/> is null.</exception>
    public Task<JobIntake> VerifyClaimedAsync(BusFile claimed, CancellationToken ct) => VerifyIntakeAsync(claimed, ct);

    /// <summary>§4 state machine row 2: <c>jobs/&lt;id&gt;.md</c> → <c>jobs/claimed/&lt;id&gt;.md</c> in one commit <c>&lt;tenant&gt; claim &lt;id&gt; &lt;from&gt;→&lt;to&gt;</c>.</summary>
    /// <param name="job">The verified job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job does not sit in this node's own inbox (a caller bug, §4 write rules).</exception>
    public Task<WriteOutcome> ClaimAsync(AcceptedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        string from = job.File.RelativePath;
        string to = BusPaths.EnvelopeFile(BusPaths.Claimed(_me.Tenant, _me.Machine), job.File.Id);
        AssertMayWrite(from, BusFolder.Jobs);
        AssertMayWrite(to, BusFolder.Claimed);
        return WriteAsync(
            Subject("claim", job.File.Id, job.Job.Header.From.Value, job.Job.Header.To.Value),
            [from, to],
            async token => Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false)),
            ct);
    }

    /// <summary>
    /// §4 state machine row 3: writes a signed report addressed to the job's sender under <c>reports/</c> and removes the
    /// claimed job, in one commit <c>&lt;tenant&gt; report &lt;ulid&gt; &lt;me&gt;→&lt;from&gt;</c>.
    /// </summary>
    /// <param name="claimed">The claimed job being answered.</param>
    /// <param name="report">The report fields.</param>
    /// <param name="body">The report body (data, never instructions).</param>
    /// <param name="signWith">This machine's signing key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome; <c>WriteFailed("sign", …)</c> when the key is absent.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public Task<WriteOutcome> ReportAsync(AcceptedJob claimed, ReportFields report, ReadOnlyMemory<byte> body, KeyId signWith, CancellationToken ct) =>
        ReportAsync(claimed, report, body, null, signWith, ct);

    /// <summary>
    /// The report write with spec 59's attached file: <paramref name="attachment"/> (when given) is written as
    /// <see cref="BusPaths.AttachmentFile"/> beside the report in the same commit; <paramref name="report"/> names and hashes it.
    /// </summary>
    /// <param name="claimed">The claimed job.</param>
    /// <param name="report">The report fields, with <c>file_name</c> and <c>file_sha256</c> when a file is attached.</param>
    /// <param name="body">The report body.</param>
    /// <param name="attachment">The attached file's bytes, or <see langword="null"/>.</param>
    /// <param name="signWith">This machine's signing key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<WriteOutcome> ReportAsync(AcceptedJob claimed, ReportFields report, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte>? attachment, KeyId signWith, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(signWith);
        DateTimeOffset now = _clock.GetUtcNow();
        var id = EnvelopeId.New(now);
        var header = new EnvelopeHeader(_me.Tenant, id, _me.Machine, claimed.Job.Header.From, now, EnvelopePriority.Normal, claimed.File.Id);
        EnvelopeResult signed = await _signer.SignAsync(Model.CreateReport(header, report, body), signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, "report " + id.Value, "sign", signed.Rejection!.Detail);
            return new WriteFailed("sign", signed.Rejection.Detail);
        }

        string reportPath = BusPaths.EnvelopeFile(BusPaths.Reports(_me.Tenant, _me.Machine), id);
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(_me.Tenant, _me.Machine), claimed.File.Id);
        AssertMayWrite(reportPath, BusFolder.Reports);
        AssertMayWrite(claimedPath, BusFolder.Claimed);
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        string? attachmentPath = attachment is null ? null : BusPaths.AttachmentFile(new BusFile(reportPath, id));
        string[] added = attachmentPath is null ? [reportPath] : [reportPath, attachmentPath];
        return await WriteAsync(
            Subject("report", id, _me.Machine.Value, claimed.Job.Header.From.Value),
            [.. added, claimedPath],
            async token =>
            {
                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                if (attachmentPath is not null)
                {
                    await WriteFileAsync(attachmentPath, attachment!.Value.ToArray(), token).ConfigureAwait(false);
                }

                return Failure("add", await _git.AddAsync(_checkoutRoot, added, token).ConfigureAwait(false))
                    ?? Failure("rm", await _git.RemoveAsync(_checkoutRoot, [claimedPath], token).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// §4 "Signature": moves a refused file to <c>jobs/rejected/</c> and, when its sender could be read, adds a signed
    /// <c>status: rejected</c> report (<c>reason</c> = the §4 wire name, <c>in_reply_to</c> = the file name) in the same commit
    /// <c>&lt;tenant&gt; reject &lt;id&gt; &lt;me&gt;→&lt;from&gt;</c>; otherwise the move alone records the rejection.
    /// </summary>
    /// <param name="job">The refused intake.</param>
    /// <param name="signWith">This machine's signing key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<WriteOutcome> RejectAsync(RejectedJob job, KeyId signWith, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(signWith);
        string from = job.File.RelativePath;
        string to = BusPaths.EnvelopeFile(BusPaths.Rejected(_me.Tenant, _me.Machine), job.File.Id);
        AssertMayWrite(from, BusFolder.Jobs);
        AssertMayWrite(to, BusFolder.Rejected);
        string reason = EnvelopeWire.ToWire(job.Reason);
        LogRejected(_me.Tenant.Value, _me.Machine.Value, Path.GetFileName(from), reason, job.Field ?? "-");

        if (job.From is null)
        {
            return await WriteAsync(
                Subject("reject", job.File.Id, _me.Machine.Value, "unknown"),
                [from, to],
                async token => Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var reportId = EnvelopeId.New(now);
        var header = new EnvelopeHeader(_me.Tenant, reportId, _me.Machine, job.From, now, EnvelopePriority.Normal, job.File.Id);
        string body = "rejected: " + reason + (job.Field is null ? string.Empty : " (" + job.Field + ")") + "\n";
        EnvelopeResult signed = await _signer.SignAsync(
            Model.CreateReport(header, new ReportFields(ReportStatus.Rejected, reason, Mandate: job.Mandate, Step: job.Step), Encoding.UTF8.GetBytes(body)), signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, "reject " + job.File.Id.Value, "sign", signed.Rejection!.Detail);
            return new WriteFailed("sign", signed.Rejection.Detail);
        }

        string reportPath = BusPaths.EnvelopeFile(BusPaths.Reports(_me.Tenant, _me.Machine), reportId);
        AssertMayWrite(reportPath, BusFolder.Reports);
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        return await WriteAsync(
            Subject("reject", job.File.Id, _me.Machine.Value, job.From.Value),
            [from, to, reportPath],
            async token =>
            {
                WriteFailed? moved = Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false));
                if (moved is not null)
                {
                    return moved;
                }

                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>§4 state machine row 1 (the sender): adds a signed job to <c>nodes/&lt;to&gt;/jobs/</c> of this tenant in one commit <c>&lt;tenant&gt; job &lt;id&gt; &lt;from&gt;→&lt;to&gt;</c>.</summary>
    /// <param name="signedJob">The signed job envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedJob"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is unsigned or belongs to another tenant (a caller bug).</exception>
    public Task<WriteOutcome> SubmitAsync(Model signedJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedJob);
        if (signedJob.Header.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the job belongs to another tenant");
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedJob);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(_me.Tenant, signedJob.Header.To), signedJob.Header.Id);
        AssertMayWrite(path, BusFolder.Jobs, submission: true);
        return WriteAsync(
            Subject("job", signedJob.Header.Id, signedJob.Header.From.Value, signedJob.Header.To.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Reads this machine's own registry file from the checkout (spec 14; never another machine's or tenant's), without the write lock —
    /// a read of the checkout like <see cref="ListNewJobsAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file bytes, or <see langword="null"/> when this machine has no registry file yet.</returns>
    public async Task<byte[]?> ReadOwnRegistryAsync(CancellationToken ct)
    {
        string full = BusPaths.Resolve(_checkoutRoot, BusPaths.Registry(_me.Tenant, _me.Machine));
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// §4 "Registry file" as amended by A14-1: writes this machine's registry file in one commit <c>&lt;tenant&gt; registry &lt;machine&gt;</c>
    /// through the one write path (lock, pull, change, commit, push with the retry rule).
    /// </summary>
    /// <param name="document">The document; its tenant and machine must be this machine's.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the document belongs to another tenant or machine (a caller bug).</exception>
    public Task<WriteOutcome> PublishRegistryAsync(Registry.RegistryDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the registry document belongs to another tenant");
        }

        if (document.Machine != _me.Machine)
        {
            throw new InvalidOperationException("write rule: the registry document belongs to another machine");
        }

        byte[] bytes = document.Serialize();
        string path = BusPaths.Registry(_me.Tenant, _me.Machine);
        AssertMayWrite(path, BusFolder.Registry);
        return WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"{_me.Tenant.Value} registry {_me.Machine.Value}"),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>Redacts URL user information from a git detail and keeps its first two lines (the one redaction rule of every bus write).</summary>
    internal static string Redacted(string detail) => RedactedDetail(detail);

    /// <summary>
    /// §4 as amended by deliverable 12 (D1): adds a signed context envelope of this node, addressed to Central, to this node's own
    /// <c>nodes/&lt;me&gt;/context/</c> folder in one commit <c>&lt;tenant&gt; context &lt;id&gt; &lt;from&gt;→central</c>.
    /// </summary>
    /// <param name="signedContext">The signed context envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedContext"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is not a context, is unsigned, belongs to another tenant or was not sent by this machine (a caller bug).</exception>
    public Task<WriteOutcome> PublishContextAsync(Model signedContext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedContext);
        if (signedContext.Type != EnvelopeType.Context)
        {
            throw new InvalidOperationException("write rule: the context envelope is not a context");
        }

        if (signedContext.Signature is null)
        {
            throw new InvalidOperationException("write rule: the context envelope is unsigned");
        }

        if (signedContext.Header.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the context envelope belongs to another tenant");
        }

        if (signedContext.Header.From != _me.Machine)
        {
            throw new InvalidOperationException("write rule: the context envelope was not sent by this machine");
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedContext);
        string path = BusPaths.EnvelopeFile(BusPaths.Context(_me.Tenant, _me.Machine), signedContext.Header.Id);
        AssertMayWrite(path, BusFolder.Context);
        return WriteAsync(
            Subject("context", signedContext.Header.Id, signedContext.Header.From.Value, signedContext.Header.To.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Deliverable 18 ("Live work is visible"): adds a signed status envelope of this node, addressed to Central, to this node's own
    /// <c>nodes/&lt;me&gt;/status/</c> folder and, in the same commit <c>&lt;tenant&gt; status &lt;id&gt; &lt;me&gt;→central</c>, removes the
    /// session's previous status file and this node's own status files older than 24 hours (sessions that ended without saying so).
    /// </summary>
    /// <param name="signedStatus">The signed status envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedStatus"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is not a status, belongs to another tenant, was not sent by this machine or is unsigned (a caller bug).</exception>
    public async Task<WriteOutcome> PublishStatusAsync(Model signedStatus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedStatus);
        if (signedStatus.Type != EnvelopeType.Status || signedStatus.Status is null)
        {
            throw new InvalidOperationException("write rule: the status envelope is not a status");
        }

        if (signedStatus.Header.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the status envelope belongs to another tenant");
        }

        if (signedStatus.Header.From != _me.Machine)
        {
            throw new InvalidOperationException("write rule: the status envelope was sent by another machine");
        }

        if (signedStatus.Signature is null)
        {
            throw new InvalidOperationException("write rule: the status envelope is unsigned");
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedStatus);
        string path = BusPaths.EnvelopeFile(BusPaths.Status(_me.Tenant, _me.Machine), signedStatus.Header.Id);
        AssertMayWrite(path, BusFolder.Status);
        IReadOnlyList<string> replaced = [.. (await OwnStatusFilesAsync(signedStatus.Status.Session, signedStatus.Header.Id, ct).ConfigureAwait(false)).Select(f => f.RelativePath)];
        return await WriteAsync(
            Subject("status", signedStatus.Header.Id, signedStatus.Header.From.Value, signedStatus.Header.To.Value),
            [path, .. replaced],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                WriteFailed? added = Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
                return added is not null || replaced.Count == 0
                    ? added
                    : Failure("rm", await _git.RemoveAsync(_checkoutRoot, replaced, token).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deliverable 18: removes the status file of a session that ended from this node's own <c>status/</c> folder (and this node's own
    /// status files older than 24 hours), in one commit <c>&lt;tenant&gt; status &lt;id&gt; &lt;me&gt;→central</c>.
    /// </summary>
    /// <param name="session">The session's 8-character id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome; <c>Pushed(0)</c> when there was nothing to remove and nothing was written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="session"/> is null or empty.</exception>
    public async Task<WriteOutcome> RemoveStatusAsync(string session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        IReadOnlyList<BusFile> files = await OwnStatusFilesAsync(session, null, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return new Pushed(0);
        }

        IReadOnlyList<string> removed = [.. files.Select(f => f.RelativePath)];
        return await WriteAsync(
            Subject("status", files[0].Id, _me.Machine.Value, MachineName.Central.Value),
            removed,
            async token => Failure("rm", await _git.RemoveAsync(_checkoutRoot, removed, token).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deliverable 18 ("Questions and decisions"): adds a signed question of this node, addressed to Central, to this node's own
    /// <c>nodes/&lt;me&gt;/questions/</c> folder in one commit <c>&lt;tenant&gt; question &lt;id&gt; &lt;me&gt;→central</c>.
    /// </summary>
    /// <param name="signedQuestion">The signed question envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedQuestion"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is not a question, belongs to another tenant, was not sent by this machine or is unsigned (a caller bug).</exception>
    public Task<WriteOutcome> PublishQuestionAsync(Model signedQuestion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedQuestion);
        AssertOwnSigned(signedQuestion, EnvelopeType.Question, "question");
        byte[] bytes = EnvelopeWriter.Serialize(signedQuestion);
        string path = BusPaths.EnvelopeFile(BusPaths.Questions(_me.Tenant, _me.Machine), signedQuestion.Header.Id);
        AssertMayWrite(path, BusFolder.Questions);
        return WriteAsync(
            Subject("question", signedQuestion.Header.Id, signedQuestion.Header.From.Value, signedQuestion.Header.To.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Deliverable 18 ("Questions and decisions"): adds Central's signed decision to <c>nodes/central/decisions/</c> and, when it
    /// answers a question whose file is still in its asker's <c>questions/</c> folder, moves that file to
    /// <c>archive/&lt;YYYY-MM of the question's created&gt;/</c> — all in one commit <c>&lt;tenant&gt; decision &lt;id&gt; central→&lt;machine&gt;</c>.
    /// </summary>
    /// <param name="signedDecision">The signed decision envelope.</param>
    /// <param name="answeredQuestion">The question the decision answers, or <see langword="null"/> when it answers something else.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedDecision"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when this machine is not Central, the envelope is not a signed decision of this machine and tenant, or
    /// <paramref name="answeredQuestion"/> is not the question the decision replies to (a caller bug).
    /// </exception>
    public Task<WriteOutcome> PublishDecisionAsync(Model signedDecision, Model? answeredQuestion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedDecision);
        if (_me.Machine != MachineName.Central)
        {
            throw new InvalidOperationException("write rule: only central writes decisions");
        }

        AssertOwnSigned(signedDecision, EnvelopeType.Decision, "decision");
        byte[] bytes = EnvelopeWriter.Serialize(signedDecision);
        string path = BusPaths.EnvelopeFile(BusPaths.Decisions(_me.Tenant, _me.Machine), signedDecision.Header.Id);
        AssertMayWrite(path, BusFolder.Decisions);
        string? from = null;
        string? to = null;
        string? month = null;
        if (answeredQuestion is not null)
        {
            if (answeredQuestion.Type != EnvelopeType.Question || answeredQuestion.Header.Tenant != _me.Tenant || answeredQuestion.Header.Id != signedDecision.Header.InReplyTo)
            {
                throw new InvalidOperationException("write rule: the decision does not answer that question");
            }

            string questionPath = BusPaths.EnvelopeFile(BusPaths.Questions(_me.Tenant, answeredQuestion.Header.From), answeredQuestion.Header.Id);
            if (File.Exists(BusPaths.Resolve(_checkoutRoot, questionPath)))
            {
                DateTime created = answeredQuestion.Header.Created.UtcDateTime;
                month = BusPaths.Archive(_me.Tenant, created.Year, created.Month);
                from = questionPath;
                to = BusPaths.EnvelopeFile(month, answeredQuestion.Header.Id);
                AssertMayArchive(from, ArchiveRole.Question);
                AssertMayArchive(to, ArchiveRole.Target);
            }
        }

        return WriteAsync(
            Subject("decision", signedDecision.Header.Id, signedDecision.Header.From.Value, signedDecision.Header.To.Value),
            from is null ? [path] : [path, from, to!],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                WriteFailed? added = Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
                if (added is not null || from is null)
                {
                    return added;
                }

                Directory.CreateDirectory(BusPaths.Resolve(_checkoutRoot, month!));
                return Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to!, token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Deliverable 18 write rule: only Central writes <c>mandates/</c>. Writes <paramref name="signedMandate"/> as
    /// <c>mandates/&lt;id&gt;.md</c> in one commit <c>&lt;tenant&gt; mandate &lt;id&gt; central→&lt;machine&gt;</c>.
    /// </summary>
    /// <param name="signedMandate">The signed mandate envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedMandate"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central or the envelope is not a signed mandate of this machine and tenant (a caller bug).</exception>
    public Task<WriteOutcome> PublishMandateAsync(Model signedMandate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedMandate);
        string path = MandatePath(signedMandate, BusFolder.Mandates);
        byte[] bytes = EnvelopeWriter.Serialize(signedMandate);
        return WriteAsync(
            Subject("mandate", signedMandate.Header.Id, signedMandate.Header.From.Value, signedMandate.Header.To.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Deliverable 18 write rule: only Central revokes. Moves <c>mandates/&lt;id&gt;.md</c> to <c>mandates/revoked/&lt;id&gt;.md</c>
    /// in one commit <c>&lt;tenant&gt; revoke &lt;id&gt; central→&lt;machine&gt;</c>; the bytes are not touched.
    /// </summary>
    /// <param name="signedMandate">The mandate as it stands in <c>mandates/</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedMandate"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central, the envelope is not a signed mandate of this tenant, or no such file is in <c>mandates/</c>.</exception>
    public Task<WriteOutcome> RevokeMandateAsync(Model signedMandate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedMandate);
        string from = MandatePath(signedMandate, BusFolder.Mandates);
        string to = MandatePath(signedMandate, BusFolder.RevokedMandates);
        if (!File.Exists(BusPaths.Resolve(_checkoutRoot, from)))
        {
            throw new InvalidOperationException("write rule: '" + from + "' is not in mandates/");
        }

        return WriteAsync(
            Subject("revoke", signedMandate.Header.Id, signedMandate.Header.From.Value, signedMandate.Header.To.Value),
            [from, to],
            async token =>
            {
                Directory.CreateDirectory(BusPaths.Resolve(_checkoutRoot, BusPaths.RevokedMandates(_me.Tenant)));
                return Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>The mandate branch of the write rule: Central only, a signed mandate of Central and this tenant, a path in the folder named.</summary>
    private string MandatePath(Model signedMandate, BusFolder folder)
    {
        if (_me.Machine != MachineName.Central)
        {
            throw new InvalidOperationException("write rule: only central writes mandates");
        }

        AssertOwnSigned(signedMandate, EnvelopeType.Mandate, "mandate");
        string path = BusPaths.EnvelopeFile(folder == BusFolder.Mandates ? BusPaths.Mandates(_me.Tenant) : BusPaths.RevokedMandates(_me.Tenant), signedMandate.Header.Id);
        BusLocation location = LocationOf(path);
        if (location.Tenant != _me.Tenant || location.Folder != folder)
        {
            throw new InvalidOperationException("write rule: '" + path + "' is not in the " + folder + " folder");
        }

        return path;
    }

    /// <summary>The shared half of the write rule for an envelope this machine sends: the right type, this tenant, this machine, signed.</summary>
    private void AssertOwnSigned(Model envelope, EnvelopeType type, string noun)
    {
        if (envelope.Type != type)
        {
            throw new InvalidOperationException("write rule: the " + noun + " envelope is not a " + noun);
        }

        if (envelope.Header.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the " + noun + " envelope belongs to another tenant");
        }

        if (envelope.Header.From != _me.Machine)
        {
            throw new InvalidOperationException("write rule: the " + noun + " envelope was sent by another machine");
        }

        if (envelope.Signature is null)
        {
            throw new InvalidOperationException("write rule: the " + noun + " envelope is unsigned");
        }
    }

    /// <summary>This node's own status files that a write replaces: the session's earlier files and any file whose id is older than 24 hours.</summary>
    private async Task<IReadOnlyList<BusFile>> OwnStatusFilesAsync(string session, EnvelopeId? except, CancellationToken ct)
    {
        DateTimeOffset oldest = _clock.GetUtcNow() - StatusMaxAge;
        var own = new List<BusFile>();
        foreach (BusFile file in await ListFolderAsync(BusPaths.Status(_me.Tenant, _me.Machine), ct).ConfigureAwait(false))
        {
            if (file.Id == except || !BusPaths.TryParse(file.RelativePath, out _))
            {
                continue;
            }

            bool replaced = file.Id.Timestamp < oldest;
            if (!replaced)
            {
                byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
                EnvelopeResult parsed = EnvelopeParser.Parse(bytes, _me.Tenant);
                replaced = parsed.IsAccepted && parsed.Envelope!.Status?.Session == session;
            }

            if (replaced)
            {
                AssertMayWrite(file.RelativePath, BusFolder.Status);
                own.Add(file);
            }
        }

        return own;
    }

    /// <summary>
    /// §4 as amended by deliverable 41 (W41-7, W41-8): adds a signed item envelope of this node, addressed to Central, to this node's own
    /// <c>nodes/&lt;me&gt;/items/</c> folder in one commit <c>&lt;tenant&gt; item &lt;id&gt; &lt;from&gt;→central</c> (W41-9).
    /// </summary>
    /// <param name="signedItem">The signed item envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="signedItem"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is not an item, is unsigned, belongs to another tenant, was not sent by this machine or is not addressed to central (a caller bug).</exception>
    public Task<WriteOutcome> PublishItemAsync(Model signedItem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedItem);
        if (signedItem.Type != EnvelopeType.Item)
        {
            throw new InvalidOperationException("write rule: the item envelope is not an item");
        }

        if (signedItem.Signature is null)
        {
            throw new InvalidOperationException("write rule: the item envelope is unsigned");
        }

        if (signedItem.Header.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: the item envelope belongs to another tenant");
        }

        if (signedItem.Header.From != _me.Machine)
        {
            throw new InvalidOperationException("write rule: the item envelope was not sent by this machine");
        }

        if (signedItem.Header.To != MachineName.Central)
        {
            throw new InvalidOperationException("write rule: the item envelope is not addressed to central");
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedItem);
        string path = BusPaths.EnvelopeFile(BusPaths.Items(_me.Tenant, _me.Machine), signedItem.Header.Id);
        AssertMayWrite(path, BusFolder.Items);
        return WriteAsync(
            Subject("item", signedItem.Header.Id, signedItem.Header.From.Value, MachineName.Central.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Central's pickup input (spec 13): under the checkout write lock, the bytes of every top-level <c>*.md</c> in the
    /// <c>context/</c> then <c>reports/</c> folder of every machine of this tenant except <see cref="MachineName.Central"/>,
    /// machines and files in ordinal order; then (spec 41, plan A11) every such machine's <c>items/</c> files in the same order.
    /// Never another tenant's subtree; nothing is verified here.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidates.</returns>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<IReadOnlyList<PickupCandidate>> ListPickupCandidatesAsync(CancellationToken ct)
    {
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        string nodes = BusPaths.Resolve(_checkoutRoot, BusPaths.Nodes(_me.Tenant));
        if (!Directory.Exists(nodes))
        {
            return [];
        }

        var candidates = new List<PickupCandidate>();
        List<MachineName> machines = Directory.EnumerateDirectories(nodes)
            .Select(d => MachineName.TryParse(Path.GetFileName(d), out MachineName? machine) ? machine : null)
            .OfType<MachineName>()
            .Where(m => m != MachineName.Central)
            .OrderBy(m => m.Value, StringComparer.Ordinal)
            .ToList();
        foreach (MachineName machine in machines)
        {
            foreach ((string folder, BusFolder kind) in new[] { (BusPaths.Context(_me.Tenant, machine), BusFolder.Context), (BusPaths.Reports(_me.Tenant, machine), BusFolder.Reports) })
            {
                await AddCandidatesAsync(candidates, folder, machine, kind, ct).ConfigureAwait(false);
            }
        }

        // Spec 41 (plan A11): every machine's items/ after every context and report.
        foreach (MachineName machine in machines)
        {
            await AddCandidatesAsync(candidates, BusPaths.Items(_me.Tenant, machine), machine, BusFolder.Items, ct).ConfigureAwait(false);
        }

        return candidates;
    }

    /// <summary>
    /// §4 state machine as amended by deliverable 41 (W41-8) and A13-5: Central's one pass commit — each picked-up <c>reports/</c> and
    /// <c>context/</c> file moved to <c>archive/&lt;YYYY-MM of created&gt;/</c>, each settled <c>items/</c> file removed and Central's
    /// signed report on it added to <c>nodes/central/reports/</c>. Subject <c>&lt;tenant&gt; archive &lt;n&gt; central→archive</c>
    /// (n = moves + items); body: one line per move, then one <c>&lt;items path&gt; → &lt;outcome&gt;</c> line per item (W41-9).
    /// </summary>
    /// <param name="moves">The files to move and their envelopes' <c>created</c> instants.</param>
    /// <param name="items">The settled items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome; the first failed step stops the change and restores the checkout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when both lists are empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central or a path or report breaks the §4 write rule (a caller bug).</exception>
    public Task<WriteOutcome> SettleAsync(IReadOnlyList<(BusFile File, DateTimeOffset Created)> moves, IReadOnlyList<SettledItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(items);
        if (moves.Count == 0 && items.Count == 0)
        {
            throw new ArgumentException("Nothing to archive.", nameof(moves));
        }

        if (_me.Machine != MachineName.Central)
        {
            throw new InvalidOperationException("write rule: only central archives");
        }

        var moved = new List<(string From, string To, string Month)>(moves.Count);
        foreach ((BusFile file, DateTimeOffset created) in moves)
        {
            DateTime utc = created.UtcDateTime;
            string month = BusPaths.Archive(_me.Tenant, utc.Year, utc.Month);
            string to = BusPaths.EnvelopeFile(month, file.Id);
            AssertMayArchive(file.RelativePath, ArchiveRole.Source);
            AssertMayArchive(to, ArchiveRole.Target);
            moved.Add((file.RelativePath, to, month));
        }

        // Spec 59 (Q2): a report's attached file moves with it, beside the archived report; it is not counted as a move of its own.
        var attached = new List<(string From, string To, string Month)>();
        foreach (((BusFile file, _), (_, string to, string month)) in moves.Zip(moved))
        {
            // A file whose name is not <ULID>.md (a stray file in reports/) has no attachment; it moves as before.
            if (!file.RelativePath.EndsWith("/" + file.Id.Value + ".md", StringComparison.Ordinal))
            {
                continue;
            }

            string attachment = BusPaths.AttachmentFile(file);
            if (File.Exists(BusPaths.Resolve(_checkoutRoot, attachment)))
            {
                attached.Add((attachment, BusPaths.AttachmentFile(new BusFile(to, file.Id)), month));
            }
        }

        var settled = new List<(string Item, string Report, byte[] Bytes, string Outcome)>(items.Count);
        foreach (SettledItem item in items)
        {
            AssertMayArchive(item.Item.RelativePath, ArchiveRole.Item);
            Model report = item.SignedReport;
            if (report.Type != EnvelopeType.Report || report.Signature is null || report.Header.Tenant != _me.Tenant || report.Header.From != _me.Machine)
            {
                throw new InvalidOperationException("write rule: the report on " + item.Item.RelativePath + " is not a signed report of this machine");
            }

            string reportPath = BusPaths.EnvelopeFile(BusPaths.Reports(_me.Tenant, _me.Machine), report.Header.Id);
            AssertMayWrite(reportPath, BusFolder.Reports);
            settled.Add((item.Item.RelativePath, reportPath, EnvelopeWriter.Serialize(report), item.Outcome));
        }

        IEnumerable<string> lines = moved.Select(m => m.From + " → " + m.To).Concat(settled.Select(s => s.Item + " → " + s.Outcome));
        return WriteAsync(
            ArchiveSubject(moved.Count + settled.Count),
            [.. moved.Select(m => m.From), .. moved.Select(m => m.To), .. attached.Select(a => a.From), .. attached.Select(a => a.To), .. settled.Select(s => s.Item), .. settled.Select(s => s.Report)],
            async token =>
            {
                foreach ((string from, string to, string month) in moved.Concat(attached))
                {
                    Directory.CreateDirectory(BusPaths.Resolve(_checkoutRoot, month));
                    WriteFailed? failed = Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false));
                    if (failed is not null)
                    {
                        return failed;
                    }
                }

                if (settled.Count == 0)
                {
                    return null;
                }

                WriteFailed? removed = Failure("rm", await _git.RemoveAsync(_checkoutRoot, [.. settled.Select(s => s.Item)], token).ConfigureAwait(false));
                if (removed is not null)
                {
                    return removed;
                }

                foreach ((_, string reportPath, byte[] bytes, _) in settled)
                {
                    await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                }

                return Failure("add", await _git.AddAsync(_checkoutRoot, [.. settled.Select(s => s.Report)], token).ConfigureAwait(false));
            },
            ct,
            string.Join('\n', lines));
    }

    /// <summary>
    /// The sweep's input (spec 17): under the checkout write lock, every top-level <c>*.md</c> in the <c>jobs/claimed/</c> folder of
    /// every machine of this tenant, Central included, machines and files in ordinal order, each with its bytes and its claim time
    /// (the committer time of the newest commit that touched the path). Never another tenant's subtree; nothing is verified here.
    /// </summary>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    internal async Task<IReadOnlyList<SweepClaim>> ListClaimsForSweepAsync(CancellationToken ct)
    {
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        string nodes = BusPaths.Resolve(_checkoutRoot, BusPaths.Nodes(_me.Tenant));
        if (!Directory.Exists(nodes))
        {
            return [];
        }

        var claims = new List<SweepClaim>();
        IEnumerable<MachineName> machines = Directory.EnumerateDirectories(nodes)
            .Select(d => MachineName.TryParse(Path.GetFileName(d), out MachineName? machine) ? machine : null)
            .OfType<MachineName>()
            .OrderBy(m => m.Value, StringComparer.Ordinal);
        foreach (MachineName machine in machines)
        {
            foreach (BusFile file in await ListFolderAsync(BusPaths.Claimed(_me.Tenant, machine), ct).ConfigureAwait(false))
            {
                byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
                DateTimeOffset? claimTime = await _git.LastCommitTimeAsync(_checkoutRoot, file.RelativePath, ct).ConfigureAwait(false);
                claims.Add(new SweepClaim(file, machine, bytes, claimTime));
            }
        }

        return claims;
    }

    /// <summary>
    /// The sweep's archive input (spec 17; Central's <c>decisions/</c> after its own two folders since deliverable 18): under the checkout write lock, every top-level <c>*.md</c> in the <c>reports/</c> then
    /// <c>context/</c> folder of every machine of this tenant, Central included, machines and files in ordinal order, each with its
    /// bytes, its leniently read <c>created</c> and — only when that cannot be read — the time of the commit that added it. Never
    /// <c>items/</c> or <c>jobs/</c>, never another tenant's subtree; nothing is verified here.
    /// </summary>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    internal async Task<IReadOnlyList<SweepArchiveFile>> ListArchiveForSweepAsync(CancellationToken ct)
    {
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        string nodes = BusPaths.Resolve(_checkoutRoot, BusPaths.Nodes(_me.Tenant));
        if (!Directory.Exists(nodes))
        {
            return [];
        }

        var files = new List<SweepArchiveFile>();
        IEnumerable<MachineName> machines = Directory.EnumerateDirectories(nodes)
            .Select(d => MachineName.TryParse(Path.GetFileName(d), out MachineName? machine) ? machine : null)
            .OfType<MachineName>()
            .OrderBy(m => m.Value, StringComparer.Ordinal);
        foreach (MachineName machine in machines)
        {
            // Deliverable 18: Central's own decisions are archived like reports; a decisions/ folder anywhere else is the auditor's business.
            (string Folder, BusFolder Kind)[] folders = machine == MachineName.Central
                ? [(BusPaths.Reports(_me.Tenant, machine), BusFolder.Reports), (BusPaths.Context(_me.Tenant, machine), BusFolder.Context), (BusPaths.Decisions(_me.Tenant, machine), BusFolder.Decisions)]
                : [(BusPaths.Reports(_me.Tenant, machine), BusFolder.Reports), (BusPaths.Context(_me.Tenant, machine), BusFolder.Context)];
            foreach ((string folder, BusFolder kind) in folders)
            {
                foreach (BusFile file in await ListFolderAsync(folder, ct).ConfigureAwait(false))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
                    DateTimeOffset? created = EnvelopeParser.PeekHeader(bytes)?.Created;
                    DateTimeOffset? added = created is null ? await _git.AddedCommitTimeAsync(_checkoutRoot, file.RelativePath, ct).ConfigureAwait(false) : null;
                    files.Add(new SweepArchiveFile(file, machine, kind, bytes, created, added));
                }
            }
        }

        return files;
    }

    /// <summary>
    /// §4 state machine as amended by deliverable 17: Central's sweep writes a stale claim back into the same machine's <c>jobs/</c>
    /// as <paramref name="signedJob"/> (the job with <c>attempt</c> raised, re-signed by Central) — one <c>git mv</c>, the new bytes,
    /// one commit <c>&lt;tenant&gt; requeue &lt;id&gt; central→&lt;machine&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central, a path breaks the §4 write rule or the job is not the claim's signed job (a caller bug).</exception>
    internal Task<WriteOutcome> RequeueAsync(SweepClaim claim, Model signedJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(signedJob);
        string from = claim.File.RelativePath;
        AssertMaySweep(from, BusFolder.Claimed, claim.Folder);
        string to = BusPaths.EnvelopeFile(BusPaths.Jobs(_me.Tenant, claim.Folder), claim.File.Id);
        AssertMaySweep(to, BusFolder.Jobs, claim.Folder);
        if (signedJob.Type != EnvelopeType.Job
            || signedJob.Signature is null
            || signedJob.Header.Tenant != _me.Tenant
            || signedJob.Header.Id != claim.File.Id
            || signedJob.Header.To != claim.Folder)
        {
            throw new InvalidOperationException("write rule: the re-queued job is not the signed job of " + from);
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedJob);
        return WriteAsync(
            Subject("requeue", claim.File.Id, _me.Machine.Value, claim.Folder.Value),
            [from, to],
            async token =>
            {
                WriteFailed? moved = Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false));
                if (moved is not null)
                {
                    return moved;
                }

                await WriteFileAsync(to, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [to], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// §4 state machine as amended by deliverable 17: Central's sweep removes a stale claim that used its attempts and adds
    /// <paramref name="signedReport"/> (Central's <c>failed</c> / <c>attempts_exhausted</c> report) to Central's own <c>reports/</c>,
    /// in one commit <c>&lt;tenant&gt; report &lt;ulid&gt; central→&lt;from&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central, a path breaks the §4 write rule or the report is not a signed report of this machine (a caller bug).</exception>
    internal Task<WriteOutcome> ExhaustAsync(SweepClaim claim, Model signedReport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(signedReport);
        string claimed = claim.File.RelativePath;
        AssertMaySweep(claimed, BusFolder.Claimed, claim.Folder);
        (string reportPath, byte[] bytes) = SweepReport(claim, signedReport);
        return WriteAsync(
            Subject("report", signedReport.Header.Id, _me.Machine.Value, signedReport.Header.To.Value),
            [reportPath, claimed],
            async token =>
            {
                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false))
                    ?? Failure("rm", await _git.RemoveAsync(_checkoutRoot, [claimed], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>Deliverable 18 (slice 14): Central removes an unclaimed session task past its timeout with its <c>no_session</c> report.</summary>
    /// <remarks>
    /// One commit <c>&lt;tenant&gt; report &lt;ulid&gt; central→&lt;from&gt;</c>: <c>jobs/&lt;id&gt;.md</c> of the task's machine removed,
    /// <paramref name="signedReport"/> (Central's <c>failed</c> / <c>no_session</c> report) added to Central's own <c>reports/</c>. Only an
    /// unclaimed <c>deliver: session</c> job whose <c>created</c> plus <c>timeout_minutes</c> (default 60) lies before now.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central, the path breaks the §4 write rule, the job is not a session task past its timeout or the report is not a signed report of this machine (a caller bug).</exception>
    internal Task<WriteOutcome> ExpireSessionTaskAsync(SweepClaim task, Model signedReport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(signedReport);
        string path = task.File.RelativePath;
        AssertMaySweep(path, BusFolder.Jobs, task.Folder);
        EnvelopeResult parsed = EnvelopeParser.Parse(task.Bytes, _me.Tenant);
        if (!parsed.IsAccepted || parsed.Envelope.Job is not { Deliver: JobDelivery.Session } job)
        {
            throw new InvalidOperationException("write rule: '" + path + "' is not a session task");
        }

        if (parsed.Envelope.Header.Created + TimeSpan.FromMinutes(job.TimeoutMinutes ?? SessionTaskTimeoutMinutes) >= _clock.GetUtcNow())
        {
            throw new InvalidOperationException("write rule: '" + path + "' is not past its timeout");
        }

        (string reportPath, byte[] bytes) = SweepReport(task, signedReport);
        return WriteAsync(
            Subject("report", signedReport.Header.Id, _me.Machine.Value, signedReport.Header.To.Value),
            [reportPath, path],
            async token =>
            {
                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false))
                    ?? Failure("rm", await _git.RemoveAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>A session task's timeout when its job names none (deliverable 18).</summary>
    internal const int SessionTaskTimeoutMinutes = 60;

    /// <summary>
    /// Deliverable 18 (slice 14): under the checkout write lock, every top-level <c>*.md</c> of every machine's <c>jobs/</c> (never
    /// <c>claimed/</c>) with its bytes and its leniently read <c>created</c>; the sweep keeps the verified session tasks past their timeout.
    /// </summary>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    internal async Task<IReadOnlyList<SweepClaim>> ListSessionTasksForSweepAsync(CancellationToken ct)
    {
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        string nodes = BusPaths.Resolve(_checkoutRoot, BusPaths.Nodes(_me.Tenant));
        if (!Directory.Exists(nodes))
        {
            return [];
        }

        var tasks = new List<SweepClaim>();
        IEnumerable<MachineName> machines = Directory.EnumerateDirectories(nodes)
            .Select(d => MachineName.TryParse(Path.GetFileName(d), out MachineName? machine) ? machine : null)
            .OfType<MachineName>()
            .OrderBy(m => m.Value, StringComparer.Ordinal);
        foreach (MachineName machine in machines)
        {
            foreach (BusFile file in await ListFolderAsync(BusPaths.Jobs(_me.Tenant, machine), ct).ConfigureAwait(false))
            {
                byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
                tasks.Add(new SweepClaim(file, machine, bytes, EnvelopeParser.PeekHeader(bytes)?.Created));
            }
        }

        return tasks;
    }

    /// <summary>
    /// §4 state machine as amended by deliverable 17: Central's sweep moves a stale claim that does not verify, unchanged, to the same
    /// machine's <c>jobs/rejected/</c> and, when its sender could be read, adds <paramref name="signedReport"/> (Central's
    /// <c>rejected</c> report) in the same commit <c>&lt;tenant&gt; reject &lt;id&gt; central→&lt;from&gt;</c> (<c>unknown</c> without a report).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central, a path breaks the §4 write rule or the report is not a signed report of this machine (a caller bug).</exception>
    internal Task<WriteOutcome> RejectStaleAsync(SweepClaim claim, Model? signedReport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        string from = claim.File.RelativePath;
        AssertMaySweep(from, BusFolder.Claimed, claim.Folder);
        string to = BusPaths.EnvelopeFile(BusPaths.Rejected(_me.Tenant, claim.Folder), claim.File.Id);
        AssertMaySweep(to, BusFolder.Rejected, claim.Folder);
        if (signedReport is null)
        {
            return WriteAsync(
                Subject("reject", claim.File.Id, _me.Machine.Value, "unknown"),
                [from, to],
                async token => Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false)),
                ct);
        }

        (string reportPath, byte[] bytes) = SweepReport(claim, signedReport);
        return WriteAsync(
            Subject("reject", claim.File.Id, _me.Machine.Value, signedReport.Header.To.Value),
            [from, to, reportPath],
            async token =>
            {
                WriteFailed? moved = Failure("mv", await _git.MoveAsync(_checkoutRoot, from, to, token).ConfigureAwait(false));
                if (moved is not null)
                {
                    return moved;
                }

                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false));
            },
            ct);
    }

    // Central's report on a swept claim: signed by this machine, for this tenant, in reply to the claimed file; its path and bytes.
    private (string Path, byte[] Bytes) SweepReport(SweepClaim claim, Model report)
    {
        if (report.Type != EnvelopeType.Report
            || report.Signature is null
            || report.Header.Tenant != _me.Tenant
            || report.Header.From != _me.Machine
            || report.Header.InReplyTo != claim.File.Id)
        {
            throw new InvalidOperationException("write rule: the report on " + claim.File.RelativePath + " is not a signed report of this machine");
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Reports(_me.Tenant, _me.Machine), report.Header.Id);
        AssertMayWrite(path, BusFolder.Reports);
        return (path, EnvelopeWriter.Serialize(report));
    }

    private async Task AddCandidatesAsync(List<PickupCandidate> candidates, string folder, MachineName machine, BusFolder kind, CancellationToken ct)
    {
        foreach (BusFile file in await ListFolderAsync(folder, ct).ConfigureAwait(false))
        {
            byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
            candidates.Add(new PickupCandidate(file, machine, kind, bytes));
        }
    }

    /// <summary>
    /// §4 state machine (A13-5): Central moves processed <c>reports/</c> and <c>context/</c> files of its tenant to
    /// <c>archive/&lt;YYYY-MM of created&gt;/</c> with one <c>git mv</c> each and one commit
    /// <c>&lt;tenant&gt; archive &lt;n&gt; central→archive</c> whose body has one line per move (A13-6).
    /// </summary>
    /// <param name="files">The files and their envelopes' <c>created</c> instants.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome; the first failed move stops the change and restores the checkout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="files"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="files"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this machine is not Central or a path breaks the §4 write rule (a caller bug).</exception>
    public Task<WriteOutcome> ArchiveAsync(IReadOnlyList<(BusFile File, DateTimeOffset Created)> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("Nothing to archive.", nameof(files));
        }

        return SettleAsync(files, [], ct);
    }

    /// <summary>The sender side: the verified reports of <paramref name="reporter"/> that answer <paramref name="inReplyTo"/> and are addressed to this machine.</summary>
    /// <param name="reporter">The machine whose <c>reports/</c> folder is read.</param>
    /// <param name="inReplyTo">The job id the reports answer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching verified report envelopes, in file-name order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<IReadOnlyList<Model>> FindReportsAsync(MachineName reporter, EnvelopeId inReplyTo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(inReplyTo);
        var reports = new List<Model>();
        foreach ((string file, bool archived) in ReportFilesOf(reporter))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            EnvelopeResult result = await _signer.ParseAndVerifyAsync(bytes, _me.Tenant, ct).ConfigureAwait(false);
            if (result.IsAccepted
                && result.Envelope!.Type == EnvelopeType.Report
                && result.Envelope.Header.InReplyTo == inReplyTo
                && result.Envelope.Header.To == _me.Machine
                && (!archived || result.Envelope.Header.From == reporter))
            {
                reports.Add(result.Envelope);
            }
        }

        return reports;
    }

    /// <summary>
    /// The sender side (decision 0001 F3): the reports of <paramref name="reporter"/> that parse, answer <paramref name="inReplyTo"/>
    /// and are addressed to this machine, but whose signature does not verify (unknown key, wrong key, tampered bytes).
    /// </summary>
    /// <param name="reporter">The machine whose <c>reports/</c> folder is read.</param>
    /// <param name="inReplyTo">The job id the reports answer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One rejection per unverifiable matching report, in file-name order; empty when every matching report verifies.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<IReadOnlyList<EnvelopeRejection>> FindUnverifiedReportsAsync(MachineName reporter, EnvelopeId inReplyTo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(inReplyTo);
        var rejections = new List<EnvelopeRejection>();
        foreach ((string file, bool archived) in ReportFilesOf(reporter))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            EnvelopeResult parsed = EnvelopeParser.Parse(bytes, _me.Tenant);
            if (!parsed.IsAccepted
                || parsed.Envelope!.Type != EnvelopeType.Report
                || parsed.Envelope.Header.InReplyTo != inReplyTo
                || parsed.Envelope.Header.To != _me.Machine
                || (archived && parsed.Envelope.Header.From != reporter))
            {
                continue;
            }

            EnvelopeResult verified = await _signer.VerifyAsync(parsed.Envelope, _me.Tenant, ct).ConfigureAwait(false);
            if (!verified.IsAccepted)
            {
                rejections.Add(verified.Rejection!);
            }
        }

        return rejections;
    }

    /// <summary>
    /// The files a report of <paramref name="reporter"/> may sit in (spec 13 AC-25): its live <c>reports/</c> folder and every
    /// <c>archive/YYYY-MM/</c> month, ordered by file name across both; <c>Archived</c> marks a file whose sender is not implied by its folder.
    /// </summary>
    private List<(string Path, bool Archived)> ReportFilesOf(MachineName reporter)
    {
        var files = new List<(string Name, string Path, bool Archived)>();
        void Add(string folder, bool archived)
        {
            string full = BusPaths.Resolve(_checkoutRoot, folder);
            if (Directory.Exists(full))
            {
                files.AddRange(Directory.EnumerateFiles(full, "*" + EnvelopeExtension, SearchOption.TopDirectoryOnly).Select(f => (Path.GetFileName(f), f, archived)));
            }
        }

        Add(BusPaths.Reports(_me.Tenant, reporter), archived: false);
        foreach (string month in ArchiveMonths.Folders(_checkoutRoot, _me.Tenant))
        {
            Add(month, archived: true);
        }

        return [.. files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => (f.Path, f.Archived))];
    }

    /// <summary>
    /// Brings the remote branch into the checkout under the checkout lock (patch 0.8.1): one writer at a time across
    /// processes; a pull that git refused because another process touched the checkout is retried once after 1 s.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the pull succeeded, with git's redacted detail.</returns>
    public async Task<BusSyncResult> PullAsync(CancellationToken ct)
    {
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false);
        return handle is null
            ? new BusSyncResult(false, BusyDetail + " for " + LockTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s")
            : await PullUnlockedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The §5 item 6 safety fetch (spec 07): <c>git fetch origin</c> under the checkout's write lock, then <c>origin/&lt;branch&gt;</c>
    /// compared with <c>HEAD</c>. Never throws for git's sake; a failure is a result with git's redacted first two lines.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Unchanged, Moved (with the remote head's short SHA) or Failed (with the detail).</returns>
    public async Task<SafetyFetchResult> SafetyFetchAsync(CancellationToken ct)
    {
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false);
        if (handle is null)
        {
            return new SafetyFetchResult(SafetyFetchOutcome.Failed, "the bus checkout is busy: another zyggy process held its lock for " + LockTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        GitResult fetch = await _git.FetchAsync(_checkoutRoot, ct).ConfigureAwait(false);
        if (!fetch.Succeeded)
        {
            return new SafetyFetchResult(SafetyFetchOutcome.Failed, RedactedDetail(fetch.Stderr));
        }

        string? remote = await _git.RevParseAsync(_checkoutRoot, "origin/" + _provider.Remote.Branch, ct).ConfigureAwait(false);
        string? head = await _git.RevParseAsync(_checkoutRoot, "HEAD", ct).ConfigureAwait(false);
        if (remote is null || head is null)
        {
            return new SafetyFetchResult(SafetyFetchOutcome.Failed, "rev-parse failed");
        }

        return string.Equals(remote, head, StringComparison.Ordinal)
            ? new SafetyFetchResult(SafetyFetchOutcome.Unchanged, string.Empty)
            : new SafetyFetchResult(SafetyFetchOutcome.Moved, remote.Length > 12 ? remote[..12] : remote);
    }

    /// <summary>Takes the one-listener-per-checkout lock without waiting (patch 0.8.1).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A handle to keep for the listener's lifetime, or <see langword="null"/> when another listener holds it.</returns>
    public Task<IDisposable?> TryAcquireListenLockAsync(CancellationToken ct) =>
        BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.ListenLockName, TimeSpan.Zero, _clock, ct);

    private async Task<BusSyncResult> PullUnlockedAsync(CancellationToken ct)
    {
        BusSyncResult pull = await _provider.PullAsync(ct).ConfigureAwait(false);
        if (pull.Succeeded || !BusCheckoutLock.IsConcurrentChange(pull.Detail))
        {
            return pull;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct).ConfigureAwait(false);
        return await _provider.PullAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs the §5 write sequence under the checkout lock: pull, change, one commit with the machine identity, push with the jittered retry rule.</summary>
    private async Task<WriteOutcome> WriteAsync(string subject, IReadOnlyList<string> paths, Func<CancellationToken, Task<WriteFailed?>> change, CancellationToken ct, string? body = null)
    {
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false);
        if (handle is null)
        {
            const string Busy = BusyDetail;
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, subject, "lock", Busy);
            return new WriteFailed("lock", Busy);
        }

        BusSyncResult pull = await PullUnlockedAsync(ct).ConfigureAwait(false);
        if (!pull.Succeeded && await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false))
        {
            // An earlier unpushed commit (deferred while offline) conflicts with the remote: drop it and keep the rest (owner, 2026-11-15).
            pull = await SkipConflictingCommitsAsync(pull, ct).ConfigureAwait(false);
        }

        if (!pull.Succeeded)
        {
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, subject, "pull", pull.Detail);
            return new WriteFailed("pull", pull.Detail);
        }

        WriteFailed? failed = await change(ct).ConfigureAwait(false);
        if (failed is not null)
        {
            await RestoreAsync(paths, ct).ConfigureAwait(false);
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, subject, failed.Stage, failed.Detail);
            return failed;
        }

        GitResult commit = await _git.CommitOnlyAsync(_checkoutRoot, body is null ? subject : subject + "\n\n" + body, paths, ct).ConfigureAwait(false);
        if (!commit.Succeeded)
        {
            await RestoreAsync(paths, ct).ConfigureAwait(false);
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, subject, "commit", commit.Stderr.Trim());
            return new WriteFailed("commit", commit.Stderr.Trim());
        }

        int attempts = 1;
        PushResult push = await _provider.PushAsync(ct).ConfigureAwait(false);
        while (push is not PushSucceeded && attempts < MaxPushAttempts)
        {
            TimeSpan delay = TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(2) * _jitter();
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            BusSyncResult retryPull = await PullUnlockedAsync(ct).ConfigureAwait(false);
            if (!retryPull.Succeeded && await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false))
            {
                // Never push while a rebase is stopped: HEAD is detached at the remote tip and the push would report success.
                return await DropConflictingCommitAsync(subject, retryPull.Detail, ct).ConfigureAwait(false);
            }

            push = await _provider.PushAsync(ct).ConfigureAwait(false);
            attempts++;
        }

        if (push is PushSucceeded)
        {
            return new Pushed(attempts);
        }

        string detail = push switch
        {
            Rejected rejected => rejected.Detail,
            PushFailed pushFailed => pushFailed.Detail,
            _ => string.Empty,
        };
        LogDeferred(_me.Tenant.Value, _me.Machine.Value, subject, attempts, detail);
        return new Deferred(attempts, detail);
    }

    /// <summary>
    /// Spec 17 "Conflicting commits": the retry's <c>pull --rebase</c> stopped because a remote commit touched the same file as this
    /// write's commit. Aborts the rebase, drops this write's commit (the branch tip after the abort, since nothing else commits under the
    /// write lock) with <c>reset --keep</c> so earlier unpushed commits stay, pulls again, and reports the write as failed. Nothing is
    /// force-pushed; the checkout ends on the bus branch with no rebase in progress.
    /// </summary>
    private async Task<WriteFailed> DropConflictingCommitAsync(string subject, string detail, CancellationToken ct)
    {
        await _git.RebaseAbortAsync(_checkoutRoot, ct).ConfigureAwait(false);
        await _git.ResetKeepAsync(_checkoutRoot, "HEAD~1", ct).ConfigureAwait(false);
        BusSyncResult again = await PullUnlockedAsync(ct).ConfigureAwait(false);
        if (!again.Succeeded && await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false))
        {
            await _git.RebaseAbortAsync(_checkoutRoot, ct).ConfigureAwait(false);
        }

        LogWriteDropped(_me.Tenant.Value, _me.Machine.Value, subject, detail);
        return new WriteFailed("rebase", detail);
    }

    /// <summary>
    /// Spec 17 "Conflicting commits", first pull of a write: the <c>pull --rebase</c> stopped on an earlier unpushed commit that touches a
    /// file the remote changed. Each commit the rebase cannot apply is skipped (dropped, one Warning naming it); the other unpushed commits
    /// stay. If the rebase is still stopped after <see cref="MaxSkips"/> skips it is aborted, so no rebase is ever left in progress.
    /// </summary>
    private async Task<BusSyncResult> SkipConflictingCommitsAsync(BusSyncResult stopped, CancellationToken ct)
    {
        for (int skips = 0; skips < MaxSkips && await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false); skips++)
        {
            string dropped = await _git.RebaseStoppedSubjectAsync(_checkoutRoot, ct).ConfigureAwait(false) ?? "an unpushed commit";
            await _git.RebaseSkipAsync(_checkoutRoot, ct).ConfigureAwait(false);
            LogWriteDropped(_me.Tenant.Value, _me.Machine.Value, dropped, stopped.Detail);
        }

        if (await _git.OperationInProgressAsync(_checkoutRoot, ct).ConfigureAwait(false))
        {
            await _git.RebaseAbortAsync(_checkoutRoot, ct).ConfigureAwait(false);
            return stopped;
        }

        return new BusSyncResult(true, string.Empty);
    }

    /// <summary>Puts every path back as in <c>HEAD</c>; a path that is not in <c>HEAD</c> (a new file) is unstaged and deleted.</summary>
    private async Task RestoreAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (string path in paths)
        {
            GitResult restored = await _git.CheckoutPathsAsync(_checkoutRoot, [path], ct).ConfigureAwait(false);
            if (restored.Succeeded)
            {
                continue;
            }

            string full = BusPaths.Resolve(_checkoutRoot, path);
            if (File.Exists(full))
            {
                await _git.UnstageAsync(_checkoutRoot, path, ct).ConfigureAwait(false);
                File.Delete(full);
            }
        }
    }

    private async Task WriteFileAsync(string relativePath, byte[] bytes, CancellationToken ct)
    {
        string full = BusPaths.Resolve(_checkoutRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The §4 write rule, enforced in Core: a path this node writes must be its own tenant's and, except for a submission into
    /// another machine's inbox, its own machine's. A file whose name is not a ULID is judged by the folder it sits in.
    /// </summary>
    private void AssertMayWrite(string relativePath, BusFolder expectedFolder, bool submission = false)
    {
        BusLocation location = LocationOf(relativePath);
        if (location.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' belongs to another tenant");
        }

        if (location.Folder != expectedFolder)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' is not in the " + expectedFolder + " folder");
        }

        if (!submission && location.Machine != _me.Machine)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' belongs to another machine");
        }
    }

    /// <summary>
    /// The sweep branch of the §4 write rule (deliverable 17): only Central, only its own tenant, and only a path in the
    /// <paramref name="expectedFolder"/> (<c>jobs/claimed/</c> as the source, <c>jobs/</c> or <c>jobs/rejected/</c> as the target) of
    /// <paramref name="machine"/> — the machine whose claim is swept, so a claim never crosses to another machine.
    /// </summary>
    private void AssertMaySweep(string relativePath, BusFolder expectedFolder, MachineName machine)
    {
        if (_me.Machine != MachineName.Central)
        {
            throw new InvalidOperationException("write rule: only central sweeps");
        }

        BusLocation location = LocationOf(relativePath);
        if (location.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' belongs to another tenant");
        }

        if (location.Folder != expectedFolder)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' is not in the " + expectedFolder + " folder");
        }

        if (location.Machine != machine)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' belongs to another machine");
        }
    }

    /// <summary>The §4 location of a path; a file whose name is not a ULID is judged by the folder it sits in.</summary>
    private static BusLocation LocationOf(string relativePath)
    {
        if (BusPaths.TryParse(relativePath, out BusLocation? location))
        {
            return location;
        }

        int slash = relativePath.LastIndexOf('/');
        if (slash < 0 || !BusPaths.TryParse(relativePath[..slash], out location) || location.Id is not null)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' is not a bus path");
        }

        return location;
    }

    /// <summary>
    /// The one archive branch of the §4 write rule (A13-4, W41-7): only Central, only its own tenant, a moved source in any machine's
    /// <c>reports/</c> or <c>context/</c>, a target in <c>archive/YYYY-MM/</c>, and (spec 41) a removed item in any machine's <c>items/</c>.
    /// </summary>
    private void AssertMayArchive(string relativePath, ArchiveRole role)
    {
        if (!BusPaths.TryParse(relativePath, out BusLocation? location))
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' is not a bus path");
        }

        if (location.Tenant != _me.Tenant)
        {
            throw new InvalidOperationException("write rule: '" + relativePath + "' belongs to another tenant");
        }

        bool allowed = role switch
        {
            ArchiveRole.Source => location.Folder is BusFolder.Reports or BusFolder.Context || (location.Folder == BusFolder.Decisions && location.Machine == MachineName.Central),
            ArchiveRole.Target => location.Folder == BusFolder.Archive,
            ArchiveRole.Question => location.Folder == BusFolder.Questions,
            _ => location.Folder == BusFolder.Items,
        };
        if (!allowed)
        {
            string what = role switch
            {
                ArchiveRole.Source => "archived",
                ArchiveRole.Target => "an archive target",
                ArchiveRole.Question => "archived as an answered question",
                _ => "removed as a settled item",
            };
            throw new InvalidOperationException("write rule: '" + relativePath + "' may not be " + what);
        }
    }

    /// <summary>Git's error output fit for a result or a log line (§8): URL user information redacted, the first two non-empty lines, trimmed.</summary>
    private static string RedactedDetail(string stderr)
    {
        string text = UrlUserInfo().Replace(stderr ?? string.Empty, "://<redacted>@");
        IEnumerable<string> lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlUserInfo();

    private static WriteFailed? Failure(string stage, GitResult result) =>
        result.Succeeded ? null : new WriteFailed(stage, result.Stderr.Trim());

    private string Subject(string action, EnvelopeId id, string from, string to) =>
        string.Create(CultureInfo.InvariantCulture, $"{_me.Tenant.Value} {action} {id.Value} {from}→{to}");

    private string ArchiveSubject(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{_me.Tenant.Value} archive {count} {MachineName.Central.Value}→archive");

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tenant}/{Machine}: the bus checkout had {Count} uncommitted change(s); parked under stash {Stash}")]
    private partial void LogStashed(string tenant, string machine, int count, string stash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tenant}/{Machine}: rejected {File}: {Reason} ({Field})")]
    private partial void LogRejected(string tenant, string machine, string file, string reason, string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tenant}/{Machine}: job {Id}: the front-matter id differs from the file name")]
    private partial void LogIdDiffers(string tenant, string machine, string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Tenant}/{Machine}: '{Subject}' not pushed after {Attempts} attempt(s); the commit stays local and goes with the next write: {Detail}")]
    private partial void LogDeferred(string tenant, string machine, string subject, int attempts, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Tenant}/{Machine}: '{Subject}' failed at {Stage}; working copy restored: {Detail}")]
    private partial void LogWriteFailed(string tenant, string machine, string subject, string stage, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tenant}/{Machine}: '{Subject}' dropped: a remote commit changed the same file; checkout back on the bus branch, no rebase in progress: {Detail}")]
    private partial void LogWriteDropped(string tenant, string machine, string subject, string detail);
}

/// <summary>What a path is in Central's pass commit (the archive branch of the §4 write rule).</summary>
internal enum ArchiveRole
{
    /// <summary>A <c>reports/</c> or <c>context/</c> file, or a file of Central's <c>decisions/</c> folder (deliverable 18), moved to the archive.</summary>
    Source,

    /// <summary>Its target under <c>archive/YYYY-MM/</c>.</summary>
    Target,

    /// <summary>A settled <c>items/</c> file removed (spec 41).</summary>
    Item,

    /// <summary>A <c>questions/</c> file moved to the archive in the commit of its decision (deliverable 18).</summary>
    Question,
}
