using System.Text;

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
}
