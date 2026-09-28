using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeWireTests
{
    public static TheoryData<EnvelopeType, string> Types => new()
    {
        { EnvelopeType.Job, "job" },
        { EnvelopeType.Report, "report" },
        { EnvelopeType.Context, "context" },
    };

    public static TheoryData<EnvelopePriority, string> Priorities => new()
    {
        { EnvelopePriority.Low, "low" },
        { EnvelopePriority.Normal, "normal" },
        { EnvelopePriority.High, "high" },
    };

    public static TheoryData<ReportStatus, string> Statuses => new()
    {
        { ReportStatus.Done, "done" },
        { ReportStatus.Failed, "failed" },
        { ReportStatus.Timeout, "timeout" },
        { ReportStatus.Rejected, "rejected" },
    };

    public static TheoryData<ContextScopeKind, string> ScopeKinds => new()
    {
        { ContextScopeKind.Project, "project" },
        { ContextScopeKind.Machine, "machine" },
        { ContextScopeKind.General, "general" },
    };

    public static TheoryData<EnvelopeRejectionReason, string> Reasons => new()
    {
        { EnvelopeRejectionReason.Malformed, "malformed" },
        { EnvelopeRejectionReason.TenantMismatch, "tenant_mismatch" },
        { EnvelopeRejectionReason.SchemaUnsupported, "schema_unsupported" },
        { EnvelopeRejectionReason.KeyIdTenantMismatch, "key_id_tenant_mismatch" },
        { EnvelopeRejectionReason.MissingField, "missing_field" },
        { EnvelopeRejectionReason.InvalidField, "invalid_field" },
        { EnvelopeRejectionReason.MissingSignature, "missing_signature" },
        { EnvelopeRejectionReason.UnknownKey, "unknown_key" },
        { EnvelopeRejectionReason.InvalidSignature, "invalid_signature" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void ToWire_EnvelopeType_ReturnsSnakeCase(EnvelopeType member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out EnvelopeType back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [MemberData(nameof(Priorities))]
    public void ToWire_EnvelopePriority_ReturnsSnakeCase(EnvelopePriority member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out EnvelopePriority back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void ToWire_ReportStatus_ReturnsSnakeCase(ReportStatus member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out ReportStatus back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [MemberData(nameof(ScopeKinds))]
    public void ToWire_ContextScopeKind_ReturnsSnakeCase(ContextScopeKind member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out ContextScopeKind back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [MemberData(nameof(Reasons))]
    public void ToWire_EnvelopeRejectionReason_ReturnsSnakeCase(EnvelopeRejectionReason member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out EnvelopeRejectionReason back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [InlineData("Job")]
    [InlineData("DONE")]
    [InlineData("")]
    public void TryFromWire_Unknown_ReturnsFalse(string wire)
    {
        // Act
        bool[] results =
        [
            EnvelopeWire.TryFromWire(wire, out EnvelopeType _),
            EnvelopeWire.TryFromWire(wire, out EnvelopePriority _),
            EnvelopeWire.TryFromWire(wire, out ReportStatus _),
            EnvelopeWire.TryFromWire(wire, out ContextScopeKind _),
            EnvelopeWire.TryFromWire(wire, out EnvelopeRejectionReason _),
        ];

        // Assert
        results.Should().AllBeEquivalentTo(false);
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert
        Types.Count.Should().Be(Enum.GetValues<EnvelopeType>().Length);
        Priorities.Count.Should().Be(Enum.GetValues<EnvelopePriority>().Length);
        Statuses.Count.Should().Be(Enum.GetValues<ReportStatus>().Length);
        ScopeKinds.Count.Should().Be(Enum.GetValues<ContextScopeKind>().Length);
        Reasons.Count.Should().Be(Enum.GetValues<EnvelopeRejectionReason>().Length);
    }
}
