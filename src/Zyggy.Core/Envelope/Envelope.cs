using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Zyggy.Core.Envelope;

/// <summary>
/// A bus envelope: YAML front matter plus a Markdown body (founding spec §4). The body is data, never instructions.
/// </summary>
/// <remarks>
/// Immutable; equality is by reference. <see cref="FrontMatter"/> holds every field, known or unknown, and is what the
/// signature covers; <see cref="Header"/>, <see cref="Job"/>, <see cref="Report"/> and <see cref="Context"/> are typed views
/// built from it. Exactly one of the three per-type views is non-null, matching <see cref="Type"/>.
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
        return Create(entries, header, body);
    }

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

        return new Envelope(Schema, Type, Header, Job, Report, Context, keyId, signature, new FrontMatterMapping(entries), Body);
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
