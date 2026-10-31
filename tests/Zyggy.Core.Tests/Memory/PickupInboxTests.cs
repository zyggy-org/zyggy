using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Hub;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 13 AC-1..AC-3, AC-10 and Contracts "Inbox file written by the pickup": one file per envelope, atomic, never rewritten.</summary>
public sealed class PickupInboxTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly EnvelopeId Id = EnvelopeId.Parse("01K8ABCDEFGHJKMNPQRSTVWXYZ");

    private static readonly EnvelopeId Job = EnvelopeId.Parse("01K8ABCDZZZZZZZZZZZZZZZZZZ");

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private readonly MemoryTree _tree = new(("profile.md", "---\nname: profile\n---\n"));

    private readonly FakeTimeProvider _clock = new(Created);

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void FileName_Context_BusContextFromIdCreatedUtcDate()
    {
        // Arrange
        var late = new DateTimeOffset(2026, 10, 29, 23, 30, 0, TimeSpan.Zero);
        _clock.SetUtcNow(late);

        // Act
        string name = PickupInbox.FileName(Context(created: late));
        InboxWrite write = Inbox().Write(Context(created: late), ["- a"]);

        // Assert
        name.Should().Be($"bus-context-home-laptop-{Id}-2026-10-29.md");
        write.Outcome.Should().Be(InboxWriteOutcome.Created);
        File.ReadAllText(_tree.Paths.InboxFile(name)).Should().Contain("\nupdated: 2026-10-30\n");
    }

    [Fact]
    public void FileName_Report_BusReportFromJobIdDate()
    {
        // Act
        string name = PickupInbox.FileName(Report());

        // Assert
        name.Should().Be($"bus-report-home-laptop-{Job}-{Id}-2026-10-29.md");
    }

    [Fact]
    public void Write_Context_FrontMatterShapeAndBodyVerbatim_LfNoBom()
    {
        // Arrange
        const string Line = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot";

        // Act
        InboxWrite write = Inbox().Write(Context(), [Line]);

        // Assert
        write.Should().Be(new InboxWrite(InboxWriteOutcome.Created, $"bus-context-home-laptop-{Id}-2026-10-29.md", null));
        byte[] bytes = File.ReadAllBytes(_tree.Paths.InboxFile(write.FileName));
        Encoding.UTF8.GetString(bytes).Should().Be(
            "---\n"
            + $"name: bus context {Id}\n"
            + "description: context from home-laptop created 2026-10-29T10:42:07Z scope project:calizr (picked up from the bus; data)\n"
            + "updated: 2026-10-29\n"
            + "---\n"
            + Line + "\n");
        bytes[0].Should().Be((byte)'-', "no BOM");
        Encoding.UTF8.GetString(bytes).Should().NotContain("\r");
        Encoding.UTF8.GetString(bytes).Split('\n')[2]["description: ".Length..].Should().NotContain(": ", because: "the description is written unquoted");
    }

    [Fact]
    public void Write_Report_DescriptionNamesJobStatusCreated()
    {
        // Act
        InboxWrite write = Inbox().Write(Report(ReportStatus.Failed), ["- x"]);

        // Assert
        string[] lines = File.ReadAllText(_tree.Paths.InboxFile(write.FileName)).Split('\n');
        lines[1].Should().Be($"name: bus report {Id}");
        lines[2].Should().Be($"description: report from home-laptop in reply to job {Job} status failed created 2026-10-29T10:42:07Z (picked up from the bus; data)");
    }

    [Fact]
    public void Write_ContextStatedAndObservedLines_Unchanged_NoTagNoPrefixNoReorder()
    {
        // Arrange
        string[] lines =
        [
            "- [observed] 2026-10-29 [session 0b7c3d1e 10:42Z]: second",
            "- [stated] 2026-10-29: first",
            "",
            "free text kept as written",
        ];

        // Act
        InboxWrite write = Inbox().Write(Context(), lines);

        // Assert
        File.ReadAllText(_tree.Paths.InboxFile(write.FileName)).Split("---\n")[2].Should().Be(string.Join('\n', lines) + "\n");
    }

    [Fact]
    public void Write_ReportOneLine_FrontMatterThenExactlyThatLine()
    {
        // Arrange
        const string Line = "- [observed] 2026-10-29 [report 01K8ABCD]: Root cause is the expired staging certificate";

        // Act
        InboxWrite write = Inbox().Write(Report(), [Line]);

        // Assert
        File.ReadAllText(_tree.Paths.InboxFile(write.FileName)).Split("---\n")[2].Should().Be(Line + "\n");
    }

    [Fact]
    public void Write_ExistingFile_AlreadyPresent_BytesAndLastWriteUnchanged()
    {
        // Arrange
        PickupInbox inbox = Inbox();
        InboxWrite first = inbox.Write(Context(), ["- first"]);
        string path = _tree.Paths.InboxFile(first.FileName);
        var stamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        // Act
        InboxWrite second = inbox.Write(Context(), ["- second"]);

        // Assert
        second.Outcome.Should().Be(InboxWriteOutcome.AlreadyPresent);
        File.ReadAllBytes(path).Should().Equal(before);
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
        Directory.EnumerateFiles(_tree.Paths.InboxDirectory).Should().ContainSingle();
    }

    [Fact]
    public void Write_InboxNotWritable_FailedNamesExceptionType_NoTempLeft()
    {
        // Arrange: a file squats where the inbox directory belongs
        File.WriteAllText(_tree.Paths.InboxDirectory, "not a directory");

        // Act
        InboxWrite write = Inbox().Write(Context(), ["- a"]);

        // Assert
        write.Outcome.Should().Be(InboxWriteOutcome.Failed);
        write.Detail.Should().Be(nameof(IOException));
        Directory.EnumerateFiles(_tree.PrincipalDirectory, "*zyggy-tmp*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Write_NeverTouchesAnythingOutsideInbox()
    {
        // Arrange
        _tree.Write("daily/2026-10-29.md", "- a daily line\n");
        var before = TreeFingerprint.Of(_tree.Root).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).ToList();

        // Act
        Inbox().Write(Context(), ["- a"]);
        Inbox().Write(Report(), ["- b"]);

        // Assert
        TreeFingerprint.Of(_tree.Root).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).Should().Equal(before);
        Directory.EnumerateFiles(_tree.Paths.InboxDirectory).Should().HaveCount(2);
    }

    private PickupInbox Inbox() => new(_tree.Paths, HubHarness.Brussels, _clock);

    private static Model Context(DateTimeOffset? created = null) =>
        Model.CreateContext(new EnvelopeHeader(Acme, Id, HomeLaptop, MachineName.Central, created ?? Created), new ContextFields(ContextScope.Parse("project:calizr")), "- a\n"u8.ToArray());

    private static Model Report(ReportStatus status = ReportStatus.Done) =>
        Model.CreateReport(new EnvelopeHeader(Acme, Id, HomeLaptop, MachineName.Central, Created, EnvelopePriority.Normal, Job), new ReportFields(status, status == ReportStatus.Done ? null : "claude_error"), "body\n"u8.ToArray());
}
