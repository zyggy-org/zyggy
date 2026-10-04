using Zyggy.Core.Dream;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-25: auto/ and daily/ changes are committed as found; a file with a secret-pattern line is withheld; inbox never.</summary>
public sealed class PassThroughTests : IDisposable
{
    private readonly MemoryTree _tree = new(
        ("auto/MEMORY.md", "# auto memory\n- prefers short answers\n"),
        ("auto/notes.md", "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 pasted by mistake\n"),
        ("daily/2026-10-04.md", "- [observed] 09:15 session a1: work.\n"),
        ("inbox/remember-2026-10-04.md", "- [stated] 2026-10-04: x.\n"),
        ("private/people/carol.md", "- [stated] 2026-09-28: Carol.\n"));

    public void Dispose() => _tree.Dispose();

    private static SecretPatterns Secrets => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private PassThroughResult Classify(string status) =>
        PassThrough.Classify(GitStatus.Parse(status), MemoryTree.Alice, MemorySnapshot.Load(_tree.Paths), Secrets, new HashSet<string>());

    [Fact]
    public void Classify_AutoAndDailyChanges_Included()
    {
        // Act
        var result = Classify(" M acme/alice/auto/MEMORY.md\0?? acme/alice/daily/2026-10-04.md\0");

        // Assert
        result.Include.Should().Equal("auto/MEMORY.md", "daily/2026-10-04.md");
        result.Withheld.Should().BeEmpty();
    }

    [Fact]
    public void Classify_SecretLineInAuto_WithheldNamedOnce()
    {
        // Act
        var result = Classify(" M acme/alice/auto/MEMORY.md\0?? acme/alice/auto/notes.md\0");

        // Assert
        result.Include.Should().Equal("auto/MEMORY.md");
        result.Withheld.Should().Equal("auto/notes.md");
    }

    [Fact]
    public void Classify_InboxChanges_NeverIncluded()
    {
        // Act
        var result = Classify("?? acme/alice/inbox/remember-2026-10-04.md\0 M acme/alice/private/people/carol.md\0");

        // Assert
        result.Include.Should().BeEmpty();
    }

    [Fact]
    public void Classify_AutoFileNeverRewritten()
    {
        // Arrange
        var before = File.ReadAllBytes(_tree.Full("auto/MEMORY.md"));

        // Act
        Classify(" M acme/alice/auto/MEMORY.md\0");

        // Assert
        File.ReadAllBytes(_tree.Full("auto/MEMORY.md")).Should().Equal(before);
    }

    [Fact]
    public void Carried_DurableChanges_ReadOnlyAndUnstagedOnesCommitted()
    {
        // Act
        var carried = PassThrough.Carried(GitStatus.Parse(
            " M acme/alice/private/people/carol.md\0M  acme/alice/profile.md\0?? acme/alice/business/areas/new.md\0 M acme/alice/auto/MEMORY.md\0"),
            MemoryTree.Alice, _tree.Paths);

        // Assert
        carried.ReadOnly.Should().BeEquivalentTo("private/people/carol.md", "profile.md", "business/areas/new.md");
        carried.CommitAsFound.Should().BeEquivalentTo("private/people/carol.md", "business/areas/new.md");
    }

    [Fact]
    public void GitStatus_ParsesRenameRecords()
    {
        // Act
        var entries = GitStatus.Parse("R  acme/alice/b.md\0acme/alice/a.md\0 M acme/alice/c.md\0");

        // Assert
        entries.Select(e => e.Path).Should().Equal("acme/alice/b.md", "acme/alice/c.md");
        entries[0].Index.Should().Be('R');
        entries[1].WorkTree.Should().Be('M');
    }
}
