using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeParserGoldenTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Theory]
    [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]
    public void Parse_EveryGoldenCase_IsAccepted(string name)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md(name), Acme);

        // Assert
        result.Rejection.Should().BeNull();
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Parse_GoldenJob_YieldsTypedJobFields()
    {
        // Act
        Model envelope = Accept("job");

        // Assert
        envelope.Schema.Should().Be(1);
        envelope.Type.Should().Be(EnvelopeType.Job);
        envelope.Header.Tenant.Should().Be(Acme);
        envelope.Header.Id.Value.Should().Be("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");
        envelope.Header.From.Value.Should().Be("central");
        envelope.Header.To.Value.Should().Be("home-laptop");
        envelope.Header.Priority.Should().Be(EnvelopePriority.Normal);
        envelope.Header.Created.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero));
        envelope.Header.InReplyTo.Should().BeNull();
        envelope.Job.Should().NotBeNull();
        envelope.Job!.Project.Should().Be("calizr");
        envelope.Job.Agent.Should().Be("env-debugger");
        envelope.Job.Worktree.Should().BeTrue();
        envelope.Job.AllowedTools.Should().Equal("Read", "Grep", "Glob", "Bash(dotnet *)", "Bash(kubectl get *)");
        envelope.Job.ReportBack.Should().Equal("summary", "diff", "files_changed");
        envelope.Job.TimeoutMinutes.Should().Be(30);
        envelope.Job.Attempt.Should().Be(1);
        envelope.Job.Deadline.Should().Be(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
        envelope.KeyId.Should().Be(KeyId.Parse("acme/1"));
        envelope.Signature.Should().Be("hmac-sha256:" + Golden.Sig("job"));
        envelope.Body.ToArray().Should().Equal(Encoding.UTF8.GetBytes(
            "Investigate why staging returns HTTP 502 on /api/bookings since Friday.\nDo not change code; report root cause and a proposed fix.\n"));
        envelope.BodyText.Should().StartWith("Investigate why staging");
        envelope.Report.Should().BeNull();
        envelope.Context.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenReport_YieldsTypedReportFields()
    {
        // Act
        Model envelope = Accept("report");

        // Assert
        envelope.Type.Should().Be(EnvelopeType.Report);
        envelope.Header.InReplyTo.Should().Be(EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0"));
        envelope.Report.Should().NotBeNull();
        envelope.Report!.Status.Should().Be(ReportStatus.Failed);
        envelope.Report.Reason.Should().Be("timeout");
        envelope.Report.NumTurns.Should().Be(7);
        envelope.Report.CostUsd.Should().Be(0.42m);
        envelope.Report.DurationSeconds.Should().Be(1830);
        envelope.Report.InputTokens.Should().Be(12345);
        envelope.Report.OutputTokens.Should().Be(2345);
        envelope.Report.Model.Should().Be("test-model-1");
        envelope.Report.FilesChanged.Should().Equal("src/Api/Bookings.cs", "README.md");
        envelope.Report.DiffRef.Should().Be("3f2a9c1");
        envelope.Report.Started.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 9, 0, TimeSpan.Zero));
        envelope.Report.Finished.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 39, 30, TimeSpan.Zero));
        envelope.Job.Should().BeNull();
        envelope.Context.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenContext_YieldsScope()
    {
        // Act
        Model envelope = Accept("context");

        // Assert
        envelope.Type.Should().Be(EnvelopeType.Context);
        envelope.Context!.Scope.Should().Be(new ContextScope(ContextScopeKind.Project, "calizr"));
        envelope.KeyId.Should().Be(KeyId.Parse("acme/2"));
        envelope.Job.Should().BeNull();
        envelope.Report.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenJobNoncanonical_KeepsUnknownFieldsAndParsedText()
    {
        // Act
        Model envelope = Accept("job-noncanonical");

        // Assert
        IReadOnlyDictionary<string, FrontMatterNode> entries = envelope.FrontMatter.Entries;
        entries["x_experimental"].Should().Be(new FrontMatterScalar("a: b"));
        entries["x_empty"].Should().Be(new FrontMatterScalar(""));
        entries["x_quote"].Should().Be(new FrontMatterScalar("it's: here"));
        entries["x_null"].Should().Be(new FrontMatterScalar("null"));
        entries["deadline"].Should().Be(new FrontMatterScalar("2026-09-29T18:00:00.500+02:00"));
        FrontMatterMapping meta = entries["x_meta"].Should().BeOfType<FrontMatterMapping>().Subject;
        FrontMatterSequence steps = meta.Entries["steps"].Should().BeOfType<FrontMatterSequence>().Subject;
        steps.Items.Should().HaveCount(2).And.AllBeOfType<FrontMatterMapping>();
        meta.Entries.Keys.Should().Equal("owner", "steps", "tags");
        envelope.Job!.Worktree.Should().BeTrue();
        envelope.Job.AllowedTools.Should().Equal("Read", "Bash(dotnet *)");
        envelope.Job.Deadline.Should().Be(new DateTimeOffset(2026, 9, 29, 16, 0, 0, 500, TimeSpan.Zero));
        envelope.Job.Deadline!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Parse_GoldenContextCrlfBody_BodyIsByteExactAndFrontMatterEndedAtFirstDelimiter()
    {
        // Arrange
        byte[] expectedBody = Encoding.UTF8.GetBytes("[observed] line one\r\n---\r\n[observed] line three");

        // Act
        Model envelope = Accept("context-crlf-body");

        // Assert
        envelope.Body.ToArray().Should().Equal(expectedBody);
        envelope.Context.Should().NotBeNull();
        envelope.Context!.Scope.Kind.Should().Be(ContextScopeKind.Machine);
    }

    private static Model Accept(string name)
    {
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md(name), Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }
}
