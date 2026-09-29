using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeFactoryTests
{
    private static readonly byte[] Body = "body\n"u8.ToArray();

    public static EnvelopeHeader Header(DateTimeOffset? created = null, EnvelopeId? inReplyTo = null) => new(
        TenantId.Parse("acme"),
        EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0"),
        MachineName.Parse("central"),
        MachineName.Parse("home-laptop"),
        created ?? new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero),
        InReplyTo: inReplyTo);

    [Fact]
    public void CreateJob_SubSecondOffsetCreated_EmitsUtcWholeSecondsText()
    {
        // Arrange
        var created = new DateTimeOffset(2026, 9, 27, 16, 5, 0, 789, TimeSpan.FromHours(2));
        var deadline = new DateTimeOffset(2026, 9, 29, 20, 0, 0, 500, TimeSpan.FromHours(2));

        // Act
        Model envelope = Model.CreateJob(Header(created), new JobFields("calizr", Deadline: deadline), Body);

        // Assert
        var expectedCreated = new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);
        envelope.FrontMatter.Entries["created"].Should().Be(new FrontMatterScalar("2026-09-27T14:05:00Z"));
        envelope.Header.Created.Should().Be(expectedCreated);
        envelope.Header.Created.Offset.Should().Be(TimeSpan.Zero);
        envelope.FrontMatter.Entries["deadline"].Should().Be(new FrontMatterScalar("2026-09-29T18:00:00Z"));
        envelope.Job!.Deadline.Should().Be(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CreateJob_Defaults_EmitsSchemaPriorityAttemptWorktreeAndOmitsNulls()
    {
        // Act
        Model envelope = Model.CreateJob(Header(), new JobFields("calizr"), Body);

        // Assert
        IReadOnlyDictionary<string, FrontMatterNode> entries = envelope.FrontMatter.Entries;
        entries["schema"].Should().Be(new FrontMatterScalar("1"));
        entries["priority"].Should().Be(new FrontMatterScalar("normal"));
        entries["attempt"].Should().Be(new FrontMatterScalar("1"));
        entries["worktree"].Should().Be(new FrontMatterScalar("false"));
        entries["type"].Should().Be(new FrontMatterScalar("job"));
        entries.Keys.Should().NotContain(["agent", "deadline", "timeout_minutes", "in_reply_to", "key_id", "sig", "allowed_tools", "report_back"]);
        envelope.KeyId.Should().BeNull();
        envelope.Signature.Should().BeNull();
        envelope.Schema.Should().Be(1);
    }

    [Fact]
    public void CreateJob_EmptyLists_EmitsEmptySequences()
    {
        // Act
        Model envelope = Model.CreateJob(Header(), new JobFields("calizr", AllowedTools: [], ReportBack: []), Body);

        // Assert
        envelope.FrontMatter.Entries["allowed_tools"].Should().Be(new FrontMatterSequence([]));
        envelope.FrontMatter.Entries["report_back"].Should().Be(new FrontMatterSequence([]));
    }

    [Fact]
    public void CreateReport_DoneWithReason_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateReport(Header(inReplyTo: JobId()), new ReportFields(ReportStatus.Done, "timeout"), Body);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateReport_FailedWithoutReason_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateReport(Header(inReplyTo: JobId()), new ReportFields(ReportStatus.Failed), Body);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateReport_WithoutInReplyTo_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateReport(Header(), new ReportFields(ReportStatus.Done), Body);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateJob_EmptyProject_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateJob(Header(), new JobFields(""), Body);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateJob_InvalidUtf8Body_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateJob(Header(), new JobFields("calizr"), new byte[] { 0x61, 0xFF });

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateContext_ProjectScope_EmitsScopeText()
    {
        // Act
        Model envelope = Model.CreateContext(Header(), new ContextFields(new ContextScope(ContextScopeKind.Project, "calizr")), Body);

        // Assert
        envelope.FrontMatter.Entries["scope"].Should().Be(new FrontMatterScalar("project:calizr"));
    }

    [Theory]
    [InlineData("0.42", "0.42")]
    [InlineData("1", "1")]
    [InlineData("1.50", "1.50")]
    public void CreateReport_CostUsd_EmitsInvariantDecimal(string cost, string text)
    {
        // Arrange
        decimal value = decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        Model envelope = Model.CreateReport(Header(inReplyTo: JobId()), new ReportFields(ReportStatus.Done, CostUsd: value), Body);

        // Assert
        envelope.FrontMatter.Entries["cost_usd"].Should().Be(new FrontMatterScalar(text));
    }

    [Theory]
    [InlineData(EnvelopeType.Job)]
    [InlineData(EnvelopeType.Report)]
    [InlineData(EnvelopeType.Context)]
    public void Create_ExactlyOneTypedViewIsNonNull(EnvelopeType type)
    {
        // Act
        Model envelope = type switch
        {
            EnvelopeType.Job => Model.CreateJob(Header(), new JobFields("calizr"), Body),
            EnvelopeType.Report => Model.CreateReport(Header(inReplyTo: JobId()), new ReportFields(ReportStatus.Done), Body),
            _ => Model.CreateContext(Header(), new ContextFields(new ContextScope(ContextScopeKind.General, null)), Body),
        };

        // Assert
        envelope.Type.Should().Be(type);
        new object?[] { envelope.Job, envelope.Report, envelope.Context }.Count(v => v is not null).Should().Be(1);
    }

    [Fact]
    public void CreateJob_FullFields_TypedViewEqualsInputAndTreeCarriesEveryField()
    {
        // Arrange
        var job = new JobFields("calizr", "env-debugger", true, ["Read", "Bash(dotnet *)"], ["summary"], 30,
            new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), 2);

        // Act
        Model created = Model.CreateJob(Header(), job, Body);

        // Assert
        created.Job.Should().BeEquivalentTo(job);
        created.FrontMatter.Entries.Keys.Should().Equal(
            "agent", "allowed_tools", "attempt", "created", "deadline", "from", "id", "priority", "project", "report_back",
            "schema", "tenant", "timeout_minutes", "to", "type", "worktree");
        created.FrontMatter.Entries["allowed_tools"].Should().Be(
            new FrontMatterSequence([new FrontMatterScalar("Read"), new FrontMatterScalar("Bash(dotnet *)")]));
        created.FrontMatter.Entries["worktree"].Should().Be(new FrontMatterScalar("true"));
        created.FrontMatter.Entries["attempt"].Should().Be(new FrontMatterScalar("2"));
    }

    private static EnvelopeId JobId() => EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");
}
