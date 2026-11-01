using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 41 AC-28, AC-29, AC-33, plan 41 A9: the body of Central's report on an item, as data lines.</summary>
public sealed class PickupItemReportTests
{
    private const string Line =
        "- [stated] 2026-10-31 (project:calizr): Archived \"Staging restore notes\" (text/markdown, 2.0 KB) at archive/calizr/staging-restore-notes.txt — How the staging database was restored";

    private static readonly ArchiveOutcome Filed = ArchiveOutcome.Archived(
        "archive/calizr/staging-restore-notes.txt", "archive/calizr/staging-restore-notes.md", "/memory/acme/alice/inbox/remember-2026-10-31.md", Line, "0123456789abcdef0123456789abcdef01234567", pushed: true, note: null);

    [Fact]
    public void DoneBody_BusRoute_FourLines()
    {
        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.DoneBody(Filed, null));

        // Assert
        body.Should().Be(
            "archived: archive/calizr/staging-restore-notes.txt\n"
            + "sidecar: archive/calizr/staging-restore-notes.md\n"
            + Line + "\n"
            + "commit: 0123456789abcdef0123456789abcdef01234567 pushed\n");
    }

    [Fact]
    public void DoneBody_PushDeferred_EndsPushDeferred()
    {
        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.DoneBody(Filed with { Pushed = false }, null));

        // Assert
        body.Should().EndWith("commit: 0123456789abcdef0123456789abcdef01234567 push deferred\n");
    }

    [Fact]
    public void DoneBody_OneDriveRoute_AdoptedLine()
    {
        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.DoneBody(Filed, "Zyggy/archive/calizr/staging-restore-notes.txt"));

        // Assert
        body.Should().EndWith("pushed\nonedrive: Zyggy/archive/calizr/staging-restore-notes.txt (adopted by the next offload)\n");
    }

    [Fact]
    public void DoneBody_AlreadyFiledNothingCommitted_AlreadyFiledLine()
    {
        // Arrange
        var already = ArchiveOutcome.AlreadyFiled("archive/calizr/staging-restore-notes.txt", "archive/calizr/staging-restore-notes.md", null, pushed: true);

        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.DoneBody(already, null));

        // Assert
        body.Should().Be("archived: archive/calizr/staging-restore-notes.txt\nsidecar: archive/calizr/staging-restore-notes.md\nalready filed\n");
    }

    [Fact]
    public void RefusedBody_OneDriveRoute_CopyStaysLine()
    {
        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.RefusedBody("type_not_allowed", "image/png", "Zyggy/archive/calizr/shot.png"));

        // Assert
        body.Should().Be(
            "archive: refused: type_not_allowed (image/png)\n"
            + "the OneDrive copy at Zyggy/archive/calizr/shot.png stays; delete it in OneDrive if wanted\n");
    }

    [Fact]
    public void RefusedBody_SecretPattern_RenderedLikeArchiveVerb()
    {
        // Act
        string body = Encoding.UTF8.GetString(PickupItemReport.RefusedBody("secret_pattern", "aws-access-key (line 2)", null));

        // Assert
        body.Should().Be("archive: refused: " + ArchiveVerb.Render(ArchiveRefusal.SecretPattern, "aws-access-key (line 2)") + "\n");
        body.Should().Be("archive: refused: secret_pattern aws-access-key (line 2)\n");
    }
}
