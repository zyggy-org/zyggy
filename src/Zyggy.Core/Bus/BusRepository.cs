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

    private const string EnvelopeExtension = ".md";

    /// <summary>How long a write or pull waits for another process's checkout lock (one full write sequence incl. its push retries fits).</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

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
            return new RejectedJob(file, EnvelopeRejectionReason.WrongRecipient, "to", "to is not this machine", envelope.Header.From);
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
    public async Task<WriteOutcome> ReportAsync(AcceptedJob claimed, ReportFields report, ReadOnlyMemory<byte> body, KeyId signWith, CancellationToken ct)
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
        return await WriteAsync(
            Subject("report", id, _me.Machine.Value, claimed.Job.Header.From.Value),
            [reportPath, claimedPath],
            async token =>
            {
                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false))
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
            Model.CreateReport(header, new ReportFields(ReportStatus.Rejected, reason), Encoding.UTF8.GetBytes(body)), signWith, ct).ConfigureAwait(false);
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
        string full = BusPaths.Resolve(_checkoutRoot, BusPaths.Reports(_me.Tenant, reporter));
        if (!Directory.Exists(full))
        {
            return [];
        }

        var reports = new List<Model>();
        foreach (string file in Directory.EnumerateFiles(full, "*" + EnvelopeExtension, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            EnvelopeResult result = await _signer.ParseAndVerifyAsync(bytes, _me.Tenant, ct).ConfigureAwait(false);
            if (result.IsAccepted
                && result.Envelope!.Type == EnvelopeType.Report
                && result.Envelope.Header.InReplyTo == inReplyTo
                && result.Envelope.Header.To == _me.Machine)
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
        string full = BusPaths.Resolve(_checkoutRoot, BusPaths.Reports(_me.Tenant, reporter));
        if (!Directory.Exists(full))
        {
            return [];
        }

        var rejections = new List<EnvelopeRejection>();
        foreach (string file in Directory.EnumerateFiles(full, "*" + EnvelopeExtension, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            EnvelopeResult parsed = EnvelopeParser.Parse(bytes, _me.Tenant);
            if (!parsed.IsAccepted
                || parsed.Envelope!.Type != EnvelopeType.Report
                || parsed.Envelope.Header.InReplyTo != inReplyTo
                || parsed.Envelope.Header.To != _me.Machine)
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
    /// Brings the remote branch into the checkout under the checkout lock (patch 0.8.1): one writer at a time across
    /// processes; a pull that git refused because another process touched the checkout is retried once after 1 s.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the pull succeeded, with git's redacted detail.</returns>
    public async Task<BusSyncResult> PullAsync(CancellationToken ct)
    {
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false);
        return handle is null
            ? new BusSyncResult(false, "the bus checkout is busy: another zyggy process held its lock for " + LockTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s")
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
    private async Task<WriteOutcome> WriteAsync(string subject, IReadOnlyList<string> paths, Func<CancellationToken, Task<WriteFailed?>> change, CancellationToken ct)
    {
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false);
        if (handle is null)
        {
            const string Busy = "the bus checkout is busy: another zyggy process held its lock";
            LogWriteFailed(_me.Tenant.Value, _me.Machine.Value, subject, "lock", Busy);
            return new WriteFailed("lock", Busy);
        }

        BusSyncResult pull = await PullUnlockedAsync(ct).ConfigureAwait(false);
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

        GitResult commit = await _git.CommitOnlyAsync(_checkoutRoot, subject, paths, ct).ConfigureAwait(false);
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
            await PullUnlockedAsync(ct).ConfigureAwait(false);
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
        if (!BusPaths.TryParse(relativePath, out BusLocation? location))
        {
            int slash = relativePath.LastIndexOf('/');
            if (slash < 0 || !BusPaths.TryParse(relativePath[..slash], out location) || location.Id is not null)
            {
                throw new InvalidOperationException("write rule: '" + relativePath + "' is not a bus path");
            }
        }

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
}
