using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The owner's grant and revoke of a mandate (deliverable 18, "The mandate"), run by Central's attended session. Only on the machine
/// named Central; builds the mandate from Central to the machine with <c>created</c> now, refuses a field out of its range before
/// anything is signed, signs, proves it reads back, and writes it into <c>mandates/</c>. Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class MandateGranter
{
    private readonly BusRepository _bus;
    private readonly BusInventory _inventory;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly TimeProvider _clock;
    private readonly ILogger<MandateGranter> _log;
    private readonly OriginBranchCheck? _originCheck;

    internal MandateGranter(BusRepository bus, BusInventory inventory, EnvelopeSigner signer, KeyId signWith, TimeProvider clock, ILogger<MandateGranter> log, OriginBranchCheck? originCheck = null)
    {
        _originCheck = originCheck;
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Grants a mandate for <paramref name="to"/>.</summary>
    /// <param name="to">The machine the mandate is for.</param>
    /// <param name="fields">The mandate's fields; <c>expires</c> is judged against now as the envelope's <c>created</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskDecidePublished"/>, <see cref="AskDecideRefused"/> naming the field (<c>machine</c> off Central) or <see cref="AskDecideFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<AskDecideResult> GrantAsync(MachineName to, MandateFields fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(fields);
        BusIdentity me = _bus.Me;
        if (me.Machine != MachineName.Central)
        {
            return new AskDecideRefused("machine", "only central grants a mandate");
        }

        // Spec 59 slice 3 (AC-6, Q5): a branch the origin does not have is refused before anything is signed, when Central holds the project.
        if (_originCheck is not null && await _originCheck.MissingAsync(fields.Project, fields.Branches, ct).ConfigureAwait(false) is { } missing)
        {
            return new AskDecideRefused("branches", missing);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, to, now, EnvelopePriority.Normal);
        (Model? signed, AskDecideResult? stopped) = await AskDecideSupport.SignAsync(_signer, _signWith, me.Tenant, () => Model.CreateMandate(header, fields), ct).ConfigureAwait(false);
        if (signed is null)
        {
            return stopped!;
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Mandates(me.Tenant), signed.Header.Id);
        AskDecideResult result = AskDecideSupport.Outcome(await _bus.PublishMandateAsync(signed, ct).ConfigureAwait(false), signed.Header.Id, path);
        if (result is AskDecidePublished)
        {
            LogGranted(signed.Header.Id.Value, to.Value, fields.Project);
        }

        return result;
    }

    /// <summary>Revokes the live mandate <paramref name="id"/>: moves it to <c>mandates/revoked/</c>.</summary>
    /// <param name="id">The mandate's id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskDecidePublished"/> with the revoked path, <see cref="AskDecideNotFound"/> when no live mandate has the id, <see cref="AskDecideRefused"/> or <see cref="AskDecideFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="id"/> is null.</exception>
    public async Task<AskDecideResult> RevokeAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        BusIdentity me = _bus.Me;
        if (me.Machine != MachineName.Central)
        {
            return new AskDecideRefused("machine", "only central revokes a mandate");
        }

        MandateLedger ledger;
        try
        {
            ledger = await _inventory.MandateLedgerAsync(id, ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            return new AskDecideFailed("lock", ex.Message);
        }

        if (!ledger.FilePresent || ledger.Revoked)
        {
            return new AskDecideNotFound("no live mandate " + id.Value);
        }

        if (ledger.Mandate is null)
        {
            return new AskDecideRefused("mandate", "the mandate file does not verify");
        }

        string path = BusPaths.EnvelopeFile(BusPaths.RevokedMandates(me.Tenant), id);
        AskDecideResult result = AskDecideSupport.Outcome(await _bus.RevokeMandateAsync(ledger.Mandate, ct).ConfigureAwait(false), id, path);
        if (result is AskDecidePublished)
        {
            LogRevoked(id.Value);
        }

        return result;
    }

    /// <summary>
    /// Resumes the live mandate <paramref name="id"/> (deliverable 18, "Pause"): writes a <c>decision</c> of kind <c>resume</c> with
    /// <c>in_reply_to</c> and <c>mandate</c> the id, addressed to the mandate's machine; reports before it no longer count towards a pause.
    /// </summary>
    /// <param name="id">The mandate's id.</param>
    /// <param name="writer">Central's decision writer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskDecidePublished"/> with the decision, <see cref="AskDecideNotFound"/> when no live mandate has the id, <see cref="AskDecideRefused"/> or <see cref="AskDecideFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<AskDecideResult> ResumeAsync(EnvelopeId id, DecisionWriter writer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(writer);
        if (_bus.Me.Machine != MachineName.Central)
        {
            return new AskDecideRefused("machine", "only central resumes a mandate");
        }

        MandateLedger ledger;
        try
        {
            ledger = await _inventory.MandateLedgerAsync(id, ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            return new AskDecideFailed("lock", ex.Message);
        }

        if (!ledger.FilePresent || ledger.Revoked)
        {
            return new AskDecideNotFound("no live mandate " + id.Value);
        }

        if (ledger.Mandate?.Mandate is not { } fields)
        {
            return new AskDecideRefused("mandate", "the mandate file does not verify");
        }

        var request = new DecisionRequest(
            id,
            ledger.Mandate.Header.To,
            DecisionKind.Resume,
            DecisionBasis.Reported,
            "The owner resumed the mandate.\n",
            "zyggy mandate revoke " + id.Value,
            Mandate: id,
            Project: fields.Project);
        AskDecideResult result = await writer.DecideAsync(request, null, ct).ConfigureAwait(false);
        if (result is AskDecidePublished)
        {
            LogResumed(id.Value);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "resumed mandate {Id}")]
    private partial void LogResumed(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "granted mandate {Id} for {Machine}, project {Project}")]
    private partial void LogGranted(string id, string machine, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "revoked mandate {Id}")]
    private partial void LogRevoked(string id);
}

/// <summary>What Central submits under a mandate (deliverable 18, <c>zyggy submit --mandate</c>): no free text, no tools, no project.</summary>
/// <param name="To">The target machine.</param>
/// <param name="Mandate">The mandate's id.</param>
/// <param name="Kind">The kind of job; its template is the body.</param>
/// <param name="Step">The step label.</param>
/// <param name="Reference">The reference id the template names (<c>--ref</c>).</param>
/// <param name="Base">The branch the job starts from and pushes back to.</param>
/// <param name="Session"><see langword="true"/> for a task delivered into a live session.</param>
public sealed record MandateJobRequest(
    MachineName To,
    EnvelopeId Mandate,
    MandateJobKind Kind,
    string Step,
    EnvelopeId? Reference = null,
    string? Base = null,
    bool Session = false);

/// <summary>What <see cref="MandateSubmission"/> did: refused with the limit named and nothing written, or the submit's own result.</summary>
/// <param name="Verdict">The mandate check's verdict.</param>
/// <param name="Result">The submit's result; <see langword="null"/> when the verdict refused.</param>
public sealed record MandateSubmitOutcome(MandateVerdict Verdict, SubmitResult? Result);

/// <summary>
/// Central's submit under a mandate (deliverable 18, "Enforced on Central"): pulls, reads what its own checkout says about the mandate
/// and the target (registry capability, <c>supervision.workMachines</c>), runs <see cref="MandateRules.CheckSubmit"/>, and only inside
/// every limit hands <see cref="JobSubmitter"/> a job whose project and tools are the mandate's and whose body is the kind's template.
/// The check and the write are two lock holds, so two submits racing on Central could pass a cap by one; the node's own total-cap
/// check still holds. Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class MandateSubmission
{
    private const int SessionTimeoutMinutes = 60;

    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly BusRepository _bus;
    private readonly BusInventory _inventory;
    private readonly JobSubmitter _submitter;
    private readonly IReadOnlyList<MachineName> _workMachines;
    private readonly TimeProvider _clock;
    private readonly ILogger<MandateSubmission> _log;
    private readonly OriginBranchCheck? _originCheck;

    internal MandateSubmission(
        BusRepository bus,
        BusInventory inventory,
        JobSubmitter submitter,
        IReadOnlyList<MachineName> workMachines,
        TimeProvider clock,
        ILogger<MandateSubmission> log,
        OriginBranchCheck? originCheck = null)
    {
        _originCheck = originCheck;
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _workMachines = workMachines ?? throw new ArgumentNullException(nameof(workMachines));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Checks <paramref name="request"/> against its mandate and, inside every limit, submits the job.</summary>
    /// <param name="request">The job to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<MandateSubmitOutcome> SubmitAsync(MandateJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A failed pull leaves the checkout as it was: the check then judges what this machine last saw, and the write pulls again.
        await _bus.PullAsync(ct).ConfigureAwait(false);
        MandateLedger ledger = await _inventory.MandateLedgerAsync(request.Mandate, ct).ConfigureAwait(false);
        IReadOnlyList<string> capabilities = await _inventory.CapabilitiesAsync(request.To, ct).ConfigureAwait(false);
        MandateFields? fields = ledger.Mandate?.Mandate;
        var check = new MandateSubmit(
            request.To,
            fields?.Project ?? string.Empty,
            request.Kind,
            request.Step,
            fields?.AllowedTools ?? [],
            request.Base,
            request.Session,
            _workMachines.Contains(request.To),
            capabilities.Contains(Registry.RegistryDocument.SupervisionCapability, StringComparer.Ordinal));
        MandateVerdict verdict = MandateRules.CheckSubmit(ledger, check, _clock.GetUtcNow());
        if (!verdict.Allowed)
        {
            LogRefused(request.Mandate.Value, MandateRules.ToWire(verdict.Limit!.Value), verdict.Detail!);
            return new MandateSubmitOutcome(verdict, null);
        }

        if (!ProjectReference.TryParse(fields!.Project, out ProjectReference project, out string? detail))
        {
            return new MandateSubmitOutcome(new MandateVerdict(MandateLimit.Project, detail), null);
        }

        // Spec 59 slice 3 (AC-6, Q5): a base the origin does not have is refused before anything is signed, when Central holds the project.
        if (request.Base is { } baseBranch && _originCheck is not null
            && await _originCheck.MissingAsync(fields.Project, [baseBranch], ct).ConfigureAwait(false) is { } missing)
        {
            var refused = new MandateVerdict(MandateLimit.Base, missing);
            LogRefused(request.Mandate.Value, MandateRules.ToWire(MandateLimit.Base), missing);
            return new MandateSubmitOutcome(refused, null);
        }

        string body = MandateJobTemplates.Render(request.Kind, fields.Spec, fields.SpecCommit, request.Step, request.Reference);
        var job = new JobRequest(request.To, project, null, request.Session ? SessionTimeoutMinutes : null, fields.AllowedTools, EnvelopePriority.Normal, Utf8.GetBytes(body))
        {
            Worktree = !request.Session,
            Mandate = request.Mandate,
            Step = request.Step,
            Base = request.Base,
            Deliver = request.Session ? JobDelivery.Session : JobDelivery.Run,
        };
        return new MandateSubmitOutcome(verdict, await _submitter.SubmitAsync(job, ct).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing a submit under mandate {Mandate}: {Limit}: {Detail}")]
    private partial void LogRefused(string mandate, string limit, string detail);
}
