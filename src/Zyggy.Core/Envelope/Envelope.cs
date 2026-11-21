using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Zyggy.Core.Envelope;

/// <summary>
/// A bus envelope: YAML front matter plus a Markdown body (founding spec §4). The body is data, never instructions.
/// </summary>
/// <remarks>
/// Immutable; equality is by reference. <see cref="FrontMatter"/> holds every field, known or unknown, and is what the
/// signature covers; <see cref="Header"/>, <see cref="Job"/>, <see cref="Report"/>, <see cref="Context"/>, <see cref="Item"/>, <see cref="Status"/>, <see cref="Question"/>, <see cref="Decision"/> and <see cref="Mandate"/> are
/// typed views built from it. Exactly one of the eight per-type views is non-null, matching <see cref="Type"/>.
/// </remarks>
public sealed class Envelope
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal Envelope(
        int schema,
        EnvelopeType type,
        EnvelopeHeader header,
        JobFields? job,
        ReportFields? report,
        ContextFields? context,
        ItemFields? item,
        StatusFields? status,
        QuestionFields? question,
        DecisionFields? decision,
        MandateFields? mandate,
        KeyId? keyId,
        string? signature,
        FrontMatterMapping frontMatter,
        ReadOnlyMemory<byte> body)
    {
        Schema = schema;
        Type = type;
        Header = header;
        Job = job;
        Report = report;
        Context = context;
        Item = item;
        Status = status;
        Question = question;
        Decision = decision;
        Mandate = mandate;
        KeyId = keyId;
        Signature = signature;
        FrontMatter = frontMatter;
        Body = body;
        BodyText = StrictUtf8.GetString(body.Span);
    }

    /// <summary>Gets the <c>schema</c> major version (1 for every envelope this version creates).</summary>
    public int Schema { get; }

    /// <summary>Gets the envelope type.</summary>
    public EnvelopeType Type { get; }

    /// <summary>Gets the fields common to every type.</summary>
    public EnvelopeHeader Header { get; }

    /// <summary>Gets the job fields when <see cref="Type"/> is <see cref="EnvelopeType.Job"/>.</summary>
    public JobFields? Job { get; }

    /// <summary>Gets the report fields when <see cref="Type"/> is <see cref="EnvelopeType.Report"/>.</summary>
    public ReportFields? Report { get; }

    /// <summary>Gets the context fields when <see cref="Type"/> is <see cref="EnvelopeType.Context"/>.</summary>
    public ContextFields? Context { get; }

    /// <summary>Gets the item fields when <see cref="Type"/> is <see cref="EnvelopeType.Item"/>.</summary>
    public ItemFields? Item { get; }

    /// <summary>Gets the status fields when <see cref="Type"/> is <see cref="EnvelopeType.Status"/>.</summary>
    public StatusFields? Status { get; }

    /// <summary>Gets the question fields when <see cref="Type"/> is <see cref="EnvelopeType.Question"/>.</summary>
    public QuestionFields? Question { get; }

    /// <summary>Gets the decision fields when <see cref="Type"/> is <see cref="EnvelopeType.Decision"/>.</summary>
    public DecisionFields? Decision { get; }

    /// <summary>Gets the mandate fields when <see cref="Type"/> is <see cref="EnvelopeType.Mandate"/>.</summary>
    public MandateFields? Mandate { get; }

    /// <summary>Gets the <c>key_id</c> the envelope is signed with; <see langword="null"/> until signed.</summary>
    public KeyId? KeyId { get; }

    /// <summary>Gets the full <c>sig</c> value (<c>hmac-sha256:&lt;hex&gt;</c>); <see langword="null"/> until signed.</summary>
    public string? Signature { get; }

    /// <summary>Gets every front-matter field, including unknown ones, <c>key_id</c> and <c>sig</c>.</summary>
    public FrontMatterMapping FrontMatter { get; }

    /// <summary>Gets the body bytes after the closing delimiter line, untouched.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Gets the body decoded as UTF-8 (lossless: the body is valid UTF-8 by construction).</summary>
    public string BodyText { get; }

    /// <summary>Creates an unsigned job envelope in canonical scalar text (§4 "Mandatory fields per type").</summary>
    /// <remarks>
    /// Timestamps are truncated to whole seconds in UTC; <c>schema: 1</c>, <c>priority</c>, <c>attempt</c> and
    /// <c>worktree</c> are always written; null optionals are omitted; empty lists are written as <c>[]</c>. Sign the result
    /// before writing it.
    /// </remarks>
    /// <param name="header">The common fields.</param>
    /// <param name="job">The job fields.</param>
    /// <param name="body">The body; must be valid UTF-8.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="job"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field breaks a §4 rule or the body is not valid UTF-8.</exception>
    public static Envelope CreateJob(EnvelopeHeader header, JobFields job, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(job);
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Job);
        Add(entries, "project", job.Project);
        AddOptional(entries, "agent", job.Agent);
        Add(entries, "worktree", job.Worktree ? "true" : "false");
        AddList(entries, "allowed_tools", job.AllowedTools);
        AddList(entries, "report_back", job.ReportBack);
        AddOptional(entries, "timeout_minutes", Invariant(job.TimeoutMinutes));
        AddOptional(entries, "deadline", Timestamp(job.Deadline));
        Add(entries, "attempt", Invariant(job.Attempt));
        AddOptional(entries, "branch", job.Branch);
        AddOptional(entries, "description", job.Description);
        AddOptional(entries, "mandate", job.Mandate?.Value);
        AddOptional(entries, "step", job.Step);
        AddOptional(entries, "base", job.Base);
        AddOptional(entries, "deliver", job.Deliver == JobDelivery.Run ? null : EnvelopeWire.ToWire(job.Deliver));

        // Spec 59: a model job keeps its bytes; kind and its fields are written only for another kind.
        AddOptional(entries, "kind", job.Kind == JobKind.Model ? null : EnvelopeWire.ToWire(job.Kind));
        AddOptional(entries, "fetch_path", job.FetchPath);
        AddOptional(entries, "fetch_ref", job.FetchRef);
        AddOptional(entries, "command", job.Command);

        // Spec 59 slice 6: a job's own turn limit and the earlier job it continues; absent keys keep every older job's bytes.
        AddOptional(entries, "max_turns", Invariant(job.MaxTurns));
        AddOptional(entries, "continue", job.Continue?.Value);
        return Create(entries, header, body);
    }

    // The ArgumentException of a field rule names the wire field (spec 59), as the parser's rejection does.
    private static ArgumentException FieldError(string field, string message) => new(message, field);

    /// <summary>Creates an unsigned report envelope in canonical scalar text (§4 "Mandatory fields per type").</summary>
    /// <remarks>Same text rules as <see cref="CreateJob"/>; <see cref="EnvelopeHeader.InReplyTo"/> is mandatory.</remarks>
    /// <param name="header">The common fields, with <c>InReplyTo</c> set to the job id.</param>
    /// <param name="report">The report fields.</param>
    /// <param name="body">The body; must be valid UTF-8.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="report"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field breaks a §4 rule or the body is not valid UTF-8.</exception>
    public static Envelope CreateReport(EnvelopeHeader header, ReportFields report, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(report);

        // Spec 59 (Q2): the attached file's name and hash come together, in the shapes the parser reads.
        if (report.FileName is not null && !FetchPaths.IsFileName(report.FileName))
        {
            throw FieldError("file_name", "file_name must be one path segment of at most 200 characters.");
        }

        if (report.FileSha256 is not null && (report.FileSha256.Length != 64 || !report.FileSha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))))
        {
            throw FieldError("file_sha256", "file_sha256 must be 64 lowercase hexadecimal characters.");
        }

        if (report.FileName is null != report.FileSha256 is null)
        {
            throw FieldError(report.FileName is null ? "file_name" : "file_sha256", "file_name and file_sha256 come together.");
        }

        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Report);
        Add(entries, "status", EnvelopeWire.ToWire(report.Status));
        AddOptional(entries, "reason", report.Reason);
        AddOptional(entries, "started", Timestamp(report.Started));
        AddOptional(entries, "finished", Timestamp(report.Finished));
        AddOptional(entries, "duration_seconds", Invariant(report.DurationSeconds));
        AddOptional(entries, "cost_usd", report.CostUsd?.ToString(CultureInfo.InvariantCulture));
        AddOptional(entries, "input_tokens", Invariant(report.InputTokens));
        AddOptional(entries, "output_tokens", Invariant(report.OutputTokens));
        AddOptional(entries, "num_turns", Invariant(report.NumTurns));
        AddOptional(entries, "model", report.Model);
        AddList(entries, "files_changed", report.FilesChanged);
        AddOptional(entries, "diff_ref", report.DiffRef);
        AddOptional(entries, "mandate", report.Mandate?.Value);
        AddOptional(entries, "step", report.Step);
        AddOptional(entries, "file_name", report.FileName);
        AddOptional(entries, "file_sha256", report.FileSha256);
        return Create(entries, header, body);
    }

    /// <summary>Creates an unsigned context envelope in canonical scalar text (§4 "Mandatory fields per type").</summary>
    /// <remarks>Same text rules as <see cref="CreateJob"/>.</remarks>
    /// <param name="header">The common fields.</param>
    /// <param name="context">The context fields.</param>
    /// <param name="body">The body (one fact per line); must be valid UTF-8.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field breaks a §4 rule or the body is not valid UTF-8.</exception>
    public static Envelope CreateContext(EnvelopeHeader header, ContextFields context, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Scope, nameof(context));
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Context);
        Add(entries, "scope", context.Scope.ToString());
        return Create(entries, header, body);
    }

    /// <summary>Creates an unsigned item envelope in canonical scalar text (§4 "Mandatory fields per type", row <c>item</c>, W41-6).</summary>
    /// <remarks>
    /// Same text rules as <see cref="CreateJob"/>. For <see cref="ItemRoute.Bus"/> the body is <paramref name="fileBytes"/> encoded by
    /// <see cref="ItemBody.Encode"/>; for <see cref="ItemRoute.OneDrive"/> the body is empty and <c>drive_name</c> is written. The fields
    /// and the body are validated by the parser's rules, so <see cref="ItemFields.Size"/> and <see cref="ItemFields.Sha256"/> must match
    /// <paramref name="fileBytes"/> on the bus route.
    /// </remarks>
    /// <param name="header">The common fields.</param>
    /// <param name="item">The item fields.</param>
    /// <param name="fileBytes">The file for the bus route; empty for the OneDrive route.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="item"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a field breaks a §4 rule, the bus body does not match <c>size</c> or <c>sha256</c>, or file bytes are given for the
    /// OneDrive route.
    /// </exception>
    public static Envelope CreateItem(EnvelopeHeader header, ItemFields item, ReadOnlyMemory<byte> fileBytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Project, nameof(item));
        ArgumentNullException.ThrowIfNull(item.Slug, nameof(item));
        if (item.Via == ItemRoute.OneDrive && !fileBytes.IsEmpty)
        {
            throw new ArgumentException("A OneDrive-route item carries no file bytes; the body is empty.", nameof(fileBytes));
        }

        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Item);
        Add(entries, "via", EnvelopeWire.ToWire(item.Via));
        Add(entries, "project", item.Project.Value);
        Add(entries, "slug", item.Slug.Value);
        AddOptional(entries, "drive_name", item.DriveName);
        Add(entries, "name", item.Name);
        Add(entries, "description", item.Description);
        Add(entries, "source_name", item.SourceName);
        Add(entries, "size", item.Size.ToString(CultureInfo.InvariantCulture));
        Add(entries, "sha256", item.Sha256);
        byte[] body = item.Via == ItemRoute.Bus ? ItemBody.Encode(fileBytes.Span) : [];
        return Create(entries, header, body);
    }

    /// <summary>Creates an unsigned status envelope in canonical scalar text (deliverable 18, "Live work is visible").</summary>
    /// <remarks>
    /// Same text rules as <see cref="CreateJob"/>. The body is empty and <see cref="EnvelopeHeader.To"/> must be Central; the optional
    /// fields that are <see langword="null"/> are not written.
    /// </remarks>
    /// <param name="header">The common fields, addressed to Central.</param>
    /// <param name="status">The status fields.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="status"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field breaks its rule or the envelope is not addressed to Central.</exception>
    public static Envelope CreateStatus(EnvelopeHeader header, StatusFields status)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(status);
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Status);
        Add(entries, "project", status.Project);
        Add(entries, "session", status.Session);
        Add(entries, "since", Timestamp(status.Since));
        Add(entries, "activity", Timestamp(status.Activity));
        AddOptional(entries, "branch", status.Branch);
        AddOptional(entries, "head", status.Head);
        AddOptional(entries, "dirty", Invariant(status.Dirty));
        AddOptional(entries, "spec", status.Spec);
        AddOptional(entries, "step", status.Step);
        return Create(entries, header, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>Creates an unsigned question envelope in canonical scalar text (deliverable 18, "Questions and decisions").</summary>
    /// <remarks>Same text rules as <see cref="CreateJob"/>. <see cref="EnvelopeHeader.To"/> must be Central; the body is the question, 1–600 characters.</remarks>
    /// <param name="header">The common fields, addressed to Central.</param>
    /// <param name="question">The question fields.</param>
    /// <param name="body">The question text; must be valid UTF-8.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="question"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field or the body breaks its rule.</exception>
    public static Envelope CreateQuestion(EnvelopeHeader header, QuestionFields question, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(question);
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Question);
        Add(entries, "project", question.Project);
        Add(entries, "default", question.Default);
        if (question.Options is { Count: > 0 })
        {
            AddList(entries, "options", question.Options);
        }

        AddOptional(entries, "session", question.Session);
        AddOptional(entries, "job", question.Job?.Value);
        return Create(entries, header, body);
    }

    /// <summary>Creates an unsigned decision envelope in canonical scalar text (deliverable 18, "Questions and decisions").</summary>
    /// <remarks>
    /// Same text rules as <see cref="CreateJob"/>. <see cref="EnvelopeHeader.From"/> must be Central and
    /// <see cref="EnvelopeHeader.InReplyTo"/> is mandatory; the body says what was decided and why, 1–1200 characters.
    /// </remarks>
    /// <param name="header">The common fields, from Central, with <c>InReplyTo</c> set.</param>
    /// <param name="decision">The decision fields.</param>
    /// <param name="body">What was decided and why; must be valid UTF-8.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="decision"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field or the body breaks its rule.</exception>
    public static Envelope CreateDecision(EnvelopeHeader header, DecisionFields decision, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(decision);
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Decision);
        Add(entries, "kind", EnvelopeWire.ToWire(decision.Kind));
        Add(entries, "basis", EnvelopeWire.ToWire(decision.Basis));
        Add(entries, "undo", decision.Undo);
        AddOptional(entries, "mandate", decision.Mandate?.Value);
        AddOptional(entries, "project", decision.Project);
        AddOptional(entries, "step", decision.Step);
        if (decision.Evidence is { Count: > 0 })
        {
            AddList(entries, "evidence", decision.Evidence);
        }

        return Create(entries, header, body);
    }

    /// <summary>Creates an unsigned mandate envelope in canonical scalar text (deliverable 18, "The mandate").</summary>
    /// <remarks>
    /// Same text rules as <see cref="CreateJob"/>. <see cref="EnvelopeHeader.From"/> must be Central; the body is empty. Every
    /// field with a default is written out, so the file states the whole mandate; <c>spec</c> and <c>spec_commit</c> are written
    /// only when given.
    /// </remarks>
    /// <param name="header">The common fields, from Central, addressed to the machine.</param>
    /// <param name="mandate">The mandate fields.</param>
    /// <returns>The unsigned envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="header"/> or <paramref name="mandate"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a field breaks its rule or the envelope is not from Central.</exception>
    public static Envelope CreateMandate(EnvelopeHeader header, MandateFields mandate)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(mandate);
        Dictionary<string, FrontMatterNode> entries = Common(header, EnvelopeType.Mandate);
        Add(entries, "project", mandate.Project);
        Add(entries, "goal", mandate.Goal);
        AddOptional(entries, "spec", mandate.Spec);
        AddOptional(entries, "spec_commit", mandate.SpecCommit);
        AddList(entries, "branches", mandate.Branches);
        AddList(entries, "allowed_tools", mandate.AllowedTools ?? []);
        Add(entries, "max_jobs", Invariant(mandate.MaxJobs));
        Add(entries, "max_jobs_per_hour", Invariant(mandate.MaxJobsPerHour));
        Add(entries, "max_jobs_per_step", Invariant(mandate.MaxJobsPerStep));
        Add(entries, "expires", Timestamp(mandate.Expires));
        AddList(entries, "grants", [.. mandate.Grants.Select(EnvelopeWire.ToWire)]);
        Add(entries, "sessions", mandate.Sessions ? "true" : "false");
        return Create(entries, header, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>Returns a copy with <c>key_id</c> set to <paramref name="keyId"/> and <c>sig</c> set, or removed when null.</summary>
    internal Envelope WithSignature(KeyId keyId, string? signature)
    {
        var entries = new Dictionary<string, FrontMatterNode>(FrontMatter.Entries, StringComparer.Ordinal)
        {
            ["key_id"] = new FrontMatterScalar(keyId.ToString()),
        };
        if (signature is null)
        {
            entries.Remove("sig");
        }
        else
        {
            entries["sig"] = new FrontMatterScalar(signature);
        }

        return new Envelope(Schema, Type, Header, Job, Report, Context, Item, Status, Question, Decision, Mandate, keyId, signature, new FrontMatterMapping(entries), Body);
    }

    /// <summary>
    /// Returns an unsigned copy of this job with <c>attempt</c> set to <paramref name="attempt"/> (spec 17, §4 state machine: the
    /// sweep's re-queue). Every other front-matter field, known or unknown, and the body bytes are carried over untouched;
    /// <c>key_id</c> and <c>sig</c> are dropped, so the copy must be signed before it is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this envelope is not a job.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="attempt"/> is below 1.</exception>
    internal Envelope WithAttempt(int attempt)
    {
        if (Type != EnvelopeType.Job)
        {
            throw new InvalidOperationException("Only a job carries an attempt.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var entries = new Dictionary<string, FrontMatterNode>(FrontMatter.Entries, StringComparer.Ordinal)
        {
            ["attempt"] = new FrontMatterScalar(attempt.ToString(CultureInfo.InvariantCulture)),
        };
        entries.Remove("key_id");
        entries.Remove("sig");
        EnvelopeResult result = EnvelopeParser.Interpret(new FrontMatterMapping(entries), Body, Header.Tenant, signed: false);
        return result.IsAccepted
            ? result.Envelope
            : throw new InvalidOperationException("The job no longer reads as a job: " + result.Rejection.Detail + ".");
    }

    private static Dictionary<string, FrontMatterNode> Common(EnvelopeHeader header, EnvelopeType type)
    {
        ArgumentNullException.ThrowIfNull(header.Tenant, nameof(header));
        ArgumentNullException.ThrowIfNull(header.Id, nameof(header));
        ArgumentNullException.ThrowIfNull(header.From, nameof(header));
        ArgumentNullException.ThrowIfNull(header.To, nameof(header));
        var entries = new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal);
        Add(entries, "schema", Invariant(EnvelopeParser.SupportedSchema));
        Add(entries, "id", header.Id.Value);
        Add(entries, "tenant", header.Tenant.Value);
        Add(entries, "type", EnvelopeWire.ToWire(type));
        Add(entries, "from", header.From.Value);
        Add(entries, "to", header.To.Value);
        Add(entries, "created", Timestamp(header.Created));
        AddOptional(entries, "in_reply_to", header.InReplyTo?.Value);
        Add(entries, "priority", EnvelopeWire.ToWire(header.Priority));
        return entries;
    }

    // One validation path: the tree is interpreted by the parser's own rules, so the typed view is built from the tree.
    private static Envelope Create(Dictionary<string, FrontMatterNode> entries, EnvelopeHeader header, ReadOnlyMemory<byte> body)
    {
        if (!Utf8.IsValid(body.Span))
        {
            throw new ArgumentException("The body must be valid UTF-8.", nameof(body));
        }

        EnvelopeResult result = EnvelopeParser.Interpret(new FrontMatterMapping(entries), body.ToArray(), header.Tenant, signed: false);
        return result.IsAccepted
            ? result.Envelope
            : throw new ArgumentException(
                "Field '" + result.Rejection.Field + "' breaks §4: " + result.Rejection.Detail + ".", result.Rejection.Field ?? nameof(header));
    }

    private static void Add(Dictionary<string, FrontMatterNode> entries, string key, string? text) =>
        entries[key] = new FrontMatterScalar(text ?? string.Empty);

    private static void AddOptional(Dictionary<string, FrontMatterNode> entries, string key, string? text)
    {
        if (text is not null)
        {
            Add(entries, key, text);
        }
    }

    private static void AddList(Dictionary<string, FrontMatterNode> entries, string key, IReadOnlyList<string>? items)
    {
        if (items is not null)
        {
            entries[key] = new FrontMatterSequence(items.Select(i => new FrontMatterScalar(i ?? string.Empty)));
        }
    }

    private static string? Invariant(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
