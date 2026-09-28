using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeParserTests
{
    private const string Hex64 = "48ea5247d0f3caf56d9a03bd9d4eab8c4783b83beed3fabe0c60c479fbd10e8e";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static EnvelopeText Job => EnvelopeText.From(Golden.Md("job"));

    private static EnvelopeText Report => EnvelopeText.From(Golden.Md("report"));

    private static EnvelopeText Context => EnvelopeText.From(Golden.Md("context"));

    public static TheoryData<string, byte[], string> NotAnEnvelope => new()
    {
        { "no opening", Utf8("schema: 1\n---\nbody"), "delimiter" },
        { "no closing", Utf8("---\nschema: 1\nbody\n"), "delimiter" },
        { "bad yaml", Utf8("---\nschema: [\n---\n"), "yaml" },
        { "anchor", Job.WithLine("x_a: &a value").ToBytes(), "anchor" },
        { "alias", Job.WithLine("x_b: *a").ToBytes(), "alias" },
        { "tag", Job.WithLine("x_t: !!str value").ToBytes(), "tag" },
        { "merge", Job.WithLine("<<: {x_m: 1}").ToBytes(), "merge" },
        { "directive", Utf8("---\n%YAML 1.2\n--- \nschema: 1\n---\n"), "directive" },
        { "second document", Utf8("---\nschema: 1\n--- \ntype: job\n---\n"), "document" },
        { "root sequence", Utf8("---\n- a\n- b\n---\n"), "mapping" },
        { "root scalar", Utf8("---\nhello\n---\n"), "mapping" },
        { "empty front matter", Utf8("---\n---\nbody"), "mapping" },
        { "duplicate", Job.WithKey("tenant", "acme").ToBytes(), "duplicate" },
        { "complex key", Job.WithLine("? [a, b]\n: x").ToBytes(), "key" },
        { "front matter utf-8", [.. Utf8("---\nx_bad: "), 0xFF, .. Utf8("\n---\n")], "UTF-8" },
        { "body utf-8", Job.WithBody([0x61, 0xFF, 0x0A]).ToBytes(), "UTF-8" },
    };

    public static TheoryData<string, string, string> MandatoryFields => new()
    {
        { "job", "schema", "schema" },
        { "job", "id", "id" },
        { "job", "tenant", "tenant" },
        { "job", "type", "type" },
        { "job", "from", "from" },
        { "job", "to", "to" },
        { "job", "created", "created" },
        { "job", "key_id", "key_id" },
        { "job", "project", "project" },
        { "report", "in_reply_to", "in_reply_to" },
        { "report", "status", "status" },
        { "context", "scope", "scope" },
    };

    [Theory]
    [MemberData(nameof(NotAnEnvelope))]
    public void Parse_NotAnEnvelope_ReturnsMalformedNamingTheRule(string description, byte[] file, string ruleWord)
    {
        // Act
        EnvelopeRejection rejection = Reject(file);

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.Malformed, description);
        rejection.Field.Should().BeNull();
        rejection.Detail.Should().ContainEquivalentOf(ruleWord, description);
    }

    [Fact]
    public void Parse_BomPrefixed_IsAcceptedAndBodyExcludesBom()
    {
        // Act
        Model envelope = Accept(Job.WithBomPrefix().ToBytes());

        // Assert
        envelope.BodyText.Should().StartWith("Investigate");
    }

    [Fact]
    public void Parse_EmptyBody_IsAcceptedWithZeroLengthBody()
    {
        // Act
        Model envelope = Accept(Job.WithBody([]).ToBytes());

        // Assert
        envelope.Body.Length.Should().Be(0);
        envelope.BodyText.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TwoDelimiterLinesInBody_BothAreBody()
    {
        // Arrange
        byte[] body = Utf8("a\n---\nb\n---\n");

        // Act
        Model envelope = Accept(Job.WithBody(body).ToBytes());

        // Assert
        envelope.Body.ToArray().Should().Equal(body);
    }

    [Fact]
    public void Parse_ForeignTenant_ReturnsTenantMismatchBeforeAnythingElse()
    {
        // Arrange
        byte[] file = Job.WithValue("tenant", "globex").WithValue("schema", "2").WithoutKey("sig").ToBytes();

        // Act
        EnvelopeRejection rejection = Reject(file);

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.TenantMismatch);
        rejection.Field.Should().Be("tenant");
    }

    [Theory]
    [InlineData("Acme")]
    [InlineData("acme_1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Parse_TenantNotALabel_ReturnsInvalidFieldTenant(string tenant)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("tenant", tenant).ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("tenant");
    }

    [Fact]
    public void Parse_SchemaTwo_ReturnsSchemaUnsupported()
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("schema", "2").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.SchemaUnsupported);
        rejection.Field.Should().Be("schema");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.1")]
    [InlineData("one")]
    public void Parse_SchemaNotPositiveInteger_ReturnsInvalidField(string schema)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("schema", schema).ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("schema");
    }

    [Fact]
    public void Parse_TypeUnknown_ReturnsInvalidFieldType()
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("type", "task").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("type");
    }

    [Fact]
    public void Parse_KeyIdOfOtherTenant_ReturnsKeyIdTenantMismatch()
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("key_id", "globex/1").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.KeyIdTenantMismatch);
        rejection.Field.Should().Be("key_id");
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("acme/0")]
    public void Parse_KeyIdMalformed_ReturnsInvalidFieldKeyId(string keyId)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithValue("key_id", keyId).ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("key_id");
    }

    [Theory]
    [MemberData(nameof(MandatoryFields))]
    public void Parse_MandatoryFieldRemoved_ReturnsMissingFieldNamingIt(string golden, string key, string field)
    {
        // Arrange
        byte[] file = EnvelopeText.From(Golden.Md(golden)).WithoutKey(key).ToBytes();

        // Act
        EnvelopeRejection rejection = Reject(file);

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        rejection.Field.Should().Be(field);
    }

    [Fact]
    public void Parse_ReportInReplyToNull_ReturnsMissingField()
    {
        // Act
        EnvelopeRejection rejection = Reject(Report.WithValue("in_reply_to", "null").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        rejection.Field.Should().Be("in_reply_to");
    }

    [Theory]
    [InlineData("job", "id", "123")]
    [InlineData("job", "from", "Central")]
    [InlineData("job", "created", "2026-09-27 14:05:00")]
    [InlineData("job", "created", "2026-09-27T14:05Z")]
    [InlineData("job", "allowed_tools", "Read")]
    [InlineData("job", "allowed_tools", "[Read, '']")]
    [InlineData("job", "project", "[a]")]
    [InlineData("job", "timeout_minutes", "0")]
    [InlineData("job", "attempt", "0")]
    [InlineData("job", "worktree", "yes")]
    [InlineData("job", "priority", "urgent")]
    [InlineData("report", "cost_usd", "-1")]
    [InlineData("report", "num_turns", "-1")]
    [InlineData("report", "in_reply_to", "not-a-ulid")]
    [InlineData("context", "scope", "'project:'")]
    public void Parse_FieldOfWrongShape_ReturnsInvalidFieldNamingIt(string golden, string key, string value)
    {
        // Arrange
        byte[] file = EnvelopeText.From(Golden.Md(golden)).WithValue(key, value).ToBytes();

        // Act
        EnvelopeRejection rejection = Reject(file);

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be(key);
    }

    [Fact]
    public void Parse_ReportDoneWithReason_ReturnsInvalidFieldReason()
    {
        // Act
        EnvelopeRejection rejection = Reject(Report.WithValue("status", "done").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("reason");
    }

    [Fact]
    public void Parse_ReportFailedWithoutReason_ReturnsMissingFieldReason()
    {
        // Act
        EnvelopeRejection rejection = Reject(Report.WithoutKey("reason").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        rejection.Field.Should().Be("reason");
    }

    [Fact]
    public void Parse_ReportFailedReasonNotToken_ReturnsInvalidFieldReason()
    {
        // Act
        EnvelopeRejection rejection = Reject(Report.WithValue("reason", "'Not A Token'").ToBytes());

        // Assert
        rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        rejection.Field.Should().Be("reason");
    }

    [Fact]
    public void Parse_ReportDoneWithoutReason_IsAccepted()
    {
        // Act
        Model envelope = Accept(Report.WithValue("status", "done").WithoutKey("reason").ToBytes());

        // Assert
        envelope.Report!.Status.Should().Be(ReportStatus.Done);
        envelope.Report.Reason.Should().BeNull();
    }

    [Fact]
    public void Parse_PriorityAbsent_DefaultsToNormal()
    {
        // Act
        Model envelope = Accept(Job.WithoutKey("priority").ToBytes());

        // Assert
        envelope.Header.Priority.Should().Be(EnvelopePriority.Normal);
    }

    [Fact]
    public void Parse_InReplyToNullOnJob_ParsesAsNull()
    {
        // Act
        Model envelope = Accept(Job.WithKey("in_reply_to", "null").ToBytes());

        // Assert
        envelope.Header.InReplyTo.Should().BeNull();
    }

    [Fact]
    public void Parse_AttemptLeadingZero_ParsesAsOneAndKeepsText()
    {
        // Act
        Model envelope = Accept(Job.WithValue("attempt", "01").ToBytes());

        // Assert
        envelope.Job!.Attempt.Should().Be(1);
        envelope.FrontMatter.Entries["attempt"].Should().Be(new FrontMatterScalar("01"));
    }

    [Fact]
    public void Parse_AllowedToolsEmptyFlow_ParsesAsEmptyList()
    {
        // Act
        Model envelope = Accept(Job.WithValue("allowed_tools", "[]").ToBytes());

        // Assert
        envelope.Job!.AllowedTools.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    public void Parse_OptionalNull_ParsesAsNull(string value)
    {
        // Act
        Model envelope = Accept(Job.WithValue("deadline", value).ToBytes());

        // Assert
        envelope.Job!.Deadline.Should().BeNull();
    }

    [Fact]
    public void Parse_OptionalAbsent_ParsesAsNull()
    {
        // Act
        Model envelope = Accept(Job.WithoutKey("deadline").ToBytes());

        // Assert
        envelope.Job!.Deadline.Should().BeNull();
    }

    [Theory]
    [InlineData(null, EnvelopeRejectionReason.MissingSignature)]
    [InlineData("null", EnvelopeRejectionReason.MissingSignature)]
    [InlineData("hmac-sha1:" + Hex64, EnvelopeRejectionReason.InvalidSignature)]
    [InlineData("hmac-sha256:abc", EnvelopeRejectionReason.InvalidSignature)]
    [InlineData("hmac-sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", EnvelopeRejectionReason.InvalidSignature)]
    public void Parse_SigAbsentOrMalformed_ReturnsMissingOrInvalidSignatureWithoutEchoingValue(
        string? sig, EnvelopeRejectionReason expected)
    {
        // Arrange
        byte[] file = sig is null ? Job.WithoutKey("sig").ToBytes() : Job.WithValue("sig", sig).ToBytes();

        // Act
        EnvelopeRejection rejection = Reject(file);

        // Assert
        rejection.Reason.Should().Be(expected);
        rejection.Field.Should().Be("sig");
        if (sig is not null)
        {
            rejection.Detail.Should().NotContain(sig);
        }
    }

    [Fact]
    public void Parse_SigUppercaseHex_IsAccepted()
    {
        // Act
        Model envelope = Accept(Job.WithValue("sig", "hmac-sha256:" + Hex64.ToUpperInvariant()).ToBytes());

        // Assert
        envelope.Signature.Should().Be("hmac-sha256:" + Hex64.ToUpperInvariant());
    }

    [Fact]
    public void Parse_FrontMatterCrlfBodyLf_IsAccepted()
    {
        // Act
        Model envelope = Accept(Job.ToBytes("\r\n"));

        // Assert
        envelope.Job!.Project.Should().Be("calizr");
        envelope.BodyText.Should().NotContain("\r");
    }

    [Fact]
    public void Parse_CommentInFrontMatter_IsDroppedFromTree()
    {
        // Act
        Model envelope = Accept(Job.WithLine("# a comment").ToBytes());

        // Assert
        envelope.FrontMatter.Entries.Keys.Should().NotContain(k => k.Contains('#', StringComparison.Ordinal));
        envelope.FrontMatter.Entries.Should().HaveCount(18);
    }

    [Fact]
    public void Parse_UnknownTopLevelField_IsPreserved()
    {
        // Act
        Model envelope = Accept(Context.WithKey("x_extra", "value").ToBytes());

        // Assert
        envelope.FrontMatter.Entries["x_extra"].Should().Be(new FrontMatterScalar("value"));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static Model Accept(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }

    private static EnvelopeRejection Reject(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeFalse();
        return result.Rejection!;
    }
}
