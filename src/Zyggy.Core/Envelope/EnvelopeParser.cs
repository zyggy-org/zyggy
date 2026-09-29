using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Parses envelope files into <see cref="Envelope"/> or a rejection (founding spec §4). Pure and synchronous: it never
/// touches a secret store, so a foreign tenant's key is never looked up.
/// </summary>
/// <remarks>
/// Check order, first failure wins: malformed → <c>tenant</c> → <c>schema</c> → <c>type</c> → <c>key_id</c> → common then
/// per-type fields (§4 "Mandatory fields per type") → <c>sig</c> shape. The signature itself is verified by the signer.
/// </remarks>
public static class EnvelopeParser
{
    /// <summary>The only <c>schema</c> major version this node supports (§9 "Versioning").</summary>
    internal const int SupportedSchema = 1;

    internal const string SignaturePrefix = "hmac-sha256:";

    internal const int SignatureHexLength = 64;

    /// <summary>Parses an envelope file.</summary>
    /// <param name="file">The file bytes, read as-is from the bus.</param>
    /// <param name="expectedTenant">The receiver's tenant; an envelope of any other tenant is refused.</param>
    /// <returns>The accepted envelope, or the reason and field at fault.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expectedTenant"/> is null.</exception>
    public static EnvelopeResult Parse(ReadOnlyMemory<byte> file, TenantId expectedTenant)
    {
        ArgumentNullException.ThrowIfNull(expectedTenant);

        EnvelopeRejection? rejection = FrontMatterReader.Read(file, out FrontMatterMapping map, out ReadOnlyMemory<byte> body);
        return rejection is not null
            ? EnvelopeResult.Rejected(rejection)
            : Interpret(map, body, expectedTenant);
    }

    /// <summary>Interprets a front-matter tree; <paramref name="signed"/> false skips <c>key_id</c> and <c>sig</c> (factories).</summary>
    internal static EnvelopeResult Interpret(FrontMatterMapping map, ReadOnlyMemory<byte> body, TenantId expectedTenant, bool signed = true)
    {
        var reader = new Reader(map, signed);
        Envelope? envelope = reader.Read(body, expectedTenant);
        return envelope is not null ? EnvelopeResult.Accepted(envelope) : EnvelopeResult.Rejected(reader.Rejection!);
    }

    internal static bool IsSignatureShape(string text)
    {
        if (!text.StartsWith(SignaturePrefix, StringComparison.Ordinal) || text.Length != SignaturePrefix.Length + SignatureHexLength)
        {
            return false;
        }

        foreach (char c in text.AsSpan(SignaturePrefix.Length))
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Reader(FrontMatterMapping map, bool signed)
    {
        public EnvelopeRejection? Rejection { get; private set; }

        public Envelope? Read(ReadOnlyMemory<byte> body, TenantId expectedTenant)
        {
            // 2. tenant
            if (!Text("tenant", true, out string? tenantText))
            {
                return null;
            }

            if (!TenantId.TryParse(tenantText, out TenantId? tenant))
            {
                return Fail<Envelope>(FieldReader.Invalid("tenant", "must be a lowercase label"));
            }

            if (tenant != expectedTenant)
            {
                return Fail<Envelope>(new EnvelopeRejection(EnvelopeRejectionReason.TenantMismatch, "tenant", "tenant differs from the receiver's tenant"));
            }

            // 3. schema
            if (!Text("schema", true, out string? schemaText))
            {
                return null;
            }

            if (!FieldReader.TryInt(schemaText!, out int schema) || schema < 1)
            {
                return Fail<Envelope>(FieldReader.Invalid("schema", "must be a positive integer"));
            }

            if (schema > SupportedSchema)
            {
                return Fail<Envelope>(new EnvelopeRejection(EnvelopeRejectionReason.SchemaUnsupported, "schema", "schema major version is not supported"));
            }

            // 4. type
            if (!Text("type", true, out string? typeText))
            {
                return null;
            }

            if (!EnvelopeWire.TryFromWire(typeText, out EnvelopeType type))
            {
                return Fail<Envelope>(FieldReader.Invalid("type", "must be job, report or context"));
            }

            // 5. key_id
            KeyId? keyId = null;
            if (signed)
            {
                if (!Text("key_id", true, out string? keyIdText))
                {
                    return null;
                }

                if (!KeyId.TryParse(keyIdText, out keyId))
                {
                    return Fail<Envelope>(FieldReader.Invalid("key_id", "must be <tenant>/<n>"));
                }

                if (keyId.Tenant != tenant)
                {
                    return Fail<Envelope>(new EnvelopeRejection(EnvelopeRejectionReason.KeyIdTenantMismatch, "key_id", "key_id belongs to another tenant"));
                }
            }

            // 6. common fields, then per-type fields
            EnvelopeHeader? header = ReadHeader(tenant, type);
            if (header is null)
            {
                return null;
            }

            JobFields? job = null;
            ReportFields? report = null;
            ContextFields? context = null;
            bool ok = type switch
            {
                EnvelopeType.Job => ReadJob(out job),
                EnvelopeType.Report => ReadReport(out report),
                _ => ReadContext(out context),
            };
            if (!ok)
            {
                return null;
            }

            // 7. sig shape
            string? signature = signed ? ReadSignature() : null;
            return signed && signature is null
                ? null
                : new Envelope(schema, type, header, job, report, context, keyId, signature, map, body);
        }

        private EnvelopeHeader? ReadHeader(TenantId tenant, EnvelopeType type)
        {
            if (!Text("id", true, out string? idText))
            {
                return null;
            }

            if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
            {
                return Fail<EnvelopeHeader>(FieldReader.Invalid("id", "must be a ULID"));
            }

            MachineName? from = Machine("from");
            MachineName? to = from is null ? null : Machine("to");
            if (to is null || !Timestamp("created", true, out DateTimeOffset? created))
            {
                return null;
            }

            if (!Text("in_reply_to", type == EnvelopeType.Report, out string? inReplyToText))
            {
                return null;
            }

            EnvelopeId? inReplyTo = null;
            if (inReplyToText is not null && !EnvelopeId.TryParse(inReplyToText, out inReplyTo))
            {
                return Fail<EnvelopeHeader>(FieldReader.Invalid("in_reply_to", "must be a ULID"));
            }

            if (!Text("priority", false, out string? priorityText))
            {
                return null;
            }

            EnvelopePriority priority = EnvelopePriority.Normal;
            if (priorityText is not null && !EnvelopeWire.TryFromWire(priorityText, out priority))
            {
                return Fail<EnvelopeHeader>(FieldReader.Invalid("priority", "must be low, normal or high"));
            }

            return new EnvelopeHeader(tenant, id, from!, to, created!.Value, priority, inReplyTo);
        }

        private bool ReadJob(out JobFields? job)
        {
            job = null;
            if (!Text("project", true, out string? project)
                || !Text("agent", false, out string? agent)
                || !Bool("worktree", out bool? worktree)
                || !List("allowed_tools", out IReadOnlyList<string>? allowedTools)
                || !List("report_back", out IReadOnlyList<string>? reportBack)
                || !Int("timeout_minutes", 1, out int? timeout)
                || !Timestamp("deadline", false, out DateTimeOffset? deadline)
                || !Int("attempt", 1, out int? attempt))
            {
                return false;
            }

            job = new JobFields(project!, agent, worktree ?? false, allowedTools, reportBack, timeout, deadline, attempt ?? 1);
            return true;
        }

        private bool ReadReport(out ReportFields? report)
        {
            report = null;
            if (!Text("status", true, out string? statusText))
            {
                return false;
            }

            if (!EnvelopeWire.TryFromWire(statusText, out ReportStatus status))
            {
                return Reject(FieldReader.Invalid("status", "must be done, failed, timeout or rejected"));
            }

            if (!Text("reason", status != ReportStatus.Done, out string? reason))
            {
                return false;
            }

            if (status == ReportStatus.Done && reason is not null)
            {
                return Reject(FieldReader.Invalid("reason", "must be absent when status is done"));
            }

            if (reason is not null && !IsReasonToken(reason))
            {
                return Reject(FieldReader.Invalid("reason", "must be a snake_case token"));
            }

            if (!Timestamp("started", false, out DateTimeOffset? started)
                || !Timestamp("finished", false, out DateTimeOffset? finished)
                || !Int("duration_seconds", 0, out int? duration)
                || !Decimal("cost_usd", out decimal? cost)
                || !Int("input_tokens", 0, out int? inputTokens)
                || !Int("output_tokens", 0, out int? outputTokens)
                || !Int("num_turns", 0, out int? numTurns)
                || !Text("model", false, out string? model)
                || !List("files_changed", out IReadOnlyList<string>? filesChanged)
                || !Text("diff_ref", false, out string? diffRef))
            {
                return false;
            }

            report = new ReportFields(status, reason, started, finished, duration, cost, inputTokens, outputTokens, numTurns, model, filesChanged, diffRef);
            return true;
        }

        private bool ReadContext(out ContextFields? context)
        {
            context = null;
            if (!Text("scope", true, out string? scopeText))
            {
                return false;
            }

            if (!ContextScope.TryParse(scopeText, out ContextScope? scope))
            {
                return Reject(FieldReader.Invalid("scope", "must be project:<name>, machine or general"));
            }

            context = new ContextFields(scope);
            return true;
        }

        private string? ReadSignature()
        {
            if (!map.Entries.TryGetValue("sig", out FrontMatterNode? node) || (node is FrontMatterScalar s && FieldReader.IsNull(s.Value)))
            {
                return Fail<string>(new EnvelopeRejection(EnvelopeRejectionReason.MissingSignature, "sig", "sig is missing"));
            }

            return node is FrontMatterScalar scalar && IsSignatureShape(scalar.Value)
                ? scalar.Value
                : Fail<string>(new EnvelopeRejection(
                    EnvelopeRejectionReason.InvalidSignature, "sig", "sig must be hmac-sha256: followed by 64 hexadecimal characters"));
        }

        private static bool IsReasonToken(string text) =>
            text[0] is >= 'a' and <= 'z' && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

        private bool Text(string key, bool mandatory, out string? text) => Ok(FieldReader.Text(map, key, mandatory, out text));

        private bool List(string key, out IReadOnlyList<string>? list) => Ok(FieldReader.StringList(map, key, out list));

        private MachineName? Machine(string key)
        {
            if (!Text(key, true, out string? text))
            {
                return null;
            }

            return MachineName.TryParse(text, out MachineName? machine)
                ? machine
                : Fail<MachineName>(FieldReader.Invalid(key, "must be a lowercase label"));
        }

        private bool Timestamp(string key, bool mandatory, out DateTimeOffset? value)
        {
            value = null;
            if (!Text(key, mandatory, out string? text))
            {
                return false;
            }

            if (text is null)
            {
                return true;
            }

            if (!FieldReader.TryTimestamp(text, out DateTimeOffset parsed))
            {
                return Reject(FieldReader.Invalid(key, "must be an RFC 3339 date-time"));
            }

            value = parsed;
            return true;
        }

        private bool Int(string key, int minimum, out int? value)
        {
            value = null;
            if (!Text(key, false, out string? text))
            {
                return false;
            }

            if (text is null)
            {
                return true;
            }

            if (!FieldReader.TryInt(text, out int parsed) || parsed < minimum)
            {
                return Reject(FieldReader.Invalid(key, minimum > 0 ? "must be a positive integer" : "must be a non-negative integer"));
            }

            value = parsed;
            return true;
        }

        private bool Decimal(string key, out decimal? value)
        {
            value = null;
            if (!Text(key, false, out string? text))
            {
                return false;
            }

            if (text is null)
            {
                return true;
            }

            if (!FieldReader.TryDecimal(text, out decimal parsed) || parsed < 0)
            {
                return Reject(FieldReader.Invalid(key, "must be a non-negative decimal"));
            }

            value = parsed;
            return true;
        }

        private bool Bool(string key, out bool? value)
        {
            value = null;
            if (!Text(key, false, out string? text))
            {
                return false;
            }

            if (text is null)
            {
                return true;
            }

            if (!FieldReader.TryBool(text, out bool parsed))
            {
                return Reject(FieldReader.Invalid(key, "must be true or false"));
            }

            value = parsed;
            return true;
        }

        private bool Ok(EnvelopeRejection? rejection)
        {
            if (rejection is null)
            {
                return true;
            }

            Rejection = rejection;
            return false;
        }

        private T? Fail<T>(EnvelopeRejection rejection)
            where T : class
        {
            Rejection = rejection;
            return null;
        }

        private bool Reject(EnvelopeRejection rejection)
        {
            Rejection = rejection;
            return false;
        }
    }
}
