using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>
/// Spec 42 AC-17 and plan 42 A8: a node skill copied from the <c>zyggy-core</c> checkout into the user-scope skills directory — same bytes
/// leave it alone, a differing folder is moved to <c>&lt;LocalAppData&gt;/zyggy/backup/&lt;stamp&gt;/skills/&lt;name&gt;</c> first, nothing deleted.
/// </summary>
public sealed class SkillInstallerTests : IDisposable
{
    private const string Body = "---\nname: remember\n---\nKeep a fact the owner stated.\n";

    private readonly SetupTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    private string Source => SetupPaths.SkillSource(Path.Combine(_files.Root, "core"), "remember");

    private string ClaudeDir => Path.Combine(_files.Root, "claude");

    private string Target => SetupPaths.SkillTarget(ClaudeDir, "remember");

    private string Backup => SetupPaths.SkillBackup(_files.LocalAppData, SetupTestFiles.Stamp, "remember");

    [Fact]
    public void Apply_TargetAbsent_Copied_DoneLineNamesSource()
    {
        // Arrange
        SeedSource();

        // Act
        SetupLine line = Install();

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillRemember, SetupOutcome.Done, "copied from " + Source));
        File.ReadAllText(Path.Combine(Target, "SKILL.md")).Should().Be(Body);
        File.ReadAllText(Path.Combine(Target, "refs", "notes.md")).Should().Be("notes\n");
        Directory.Exists(Path.Combine(_files.LocalAppData, "zyggy")).Should().BeFalse();
    }

    [Fact]
    public void Apply_SameBytes_AlreadyDone_NothingWritten_LastWriteTimesUnchanged()
    {
        // Arrange
        SeedSource();
        Install();
        DateTime stamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string[] files = Directory.GetFiles(Target, "*", SearchOption.AllDirectories);
        Array.ForEach(files, f => File.SetLastWriteTimeUtc(f, stamp));

        // Act
        SetupLine line = Install();

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillRemember, SetupOutcome.AlreadyDone, null));
        files.Select(File.GetLastWriteTimeUtc).Should().AllBeEquivalentTo(stamp);
        Directory.Exists(Path.Combine(_files.LocalAppData, "zyggy")).Should().BeFalse();
    }

    [Fact]
    public void Apply_DifferentBytes_ExistingMovedToBackupOutsideSkillsDir_ThenReplaced_LineNamesBackup()
    {
        // Arrange
        SeedSource();
        Write(Path.Combine(Target, "SKILL.md"), "---\nname: remember\n---\nThe owner's own skill.\n");
        Write(Path.Combine(Target, "mine.md"), "owner file\n");

        // Act
        SetupLine line = Install();

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillRemember, SetupOutcome.Done, "copied from " + Source + " (backup " + Backup + ")"));
        Backup.Should().StartWith(Path.Combine(_files.LocalAppData, "zyggy", "backup", SetupTestFiles.Stamp, "skills"));
        File.ReadAllText(Path.Combine(Backup, "SKILL.md")).Should().Contain("The owner's own skill.");
        File.ReadAllText(Path.Combine(Backup, "mine.md")).Should().Be("owner file\n");
        SkillInstaller.SameBytes(Source, Target).Should().BeTrue();
        Directory.GetDirectories(Path.Combine(ClaudeDir, "skills")).Should().Equal(Target);
    }

    [Fact]
    public void Apply_SourceMissing_FailedZyggyCoreNotAvailable()
    {
        // Act
        SetupLine line = Install();

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillRemember, SetupOutcome.Failed, "zyggy-core not available"));
        Directory.Exists(Target).Should().BeFalse();
    }

    [Fact]
    public void Apply_SourceMissingFromAnExistingCheckout_SkillThatMayBeAbsent_LeftAsIs()
    {
        // Arrange: spec 15 slice 6 — the checkout is there but does not hold this skill yet
        string checkout = Path.Combine(_files.Root, "core");
        Directory.CreateDirectory(checkout);

        // Act
        SetupLine line = SkillInstaller.Apply(SetupItem.SkillBus, Source, Target, Backup, checkout);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillBus, SetupOutcome.LeftAsIs, "not in this zyggy-core checkout"));
        Directory.Exists(Target).Should().BeFalse();
    }

    [Fact]
    public void Apply_CheckoutItselfMissing_SkillThatMayBeAbsent_StillFailed()
    {
        // Act
        SetupLine line = SkillInstaller.Apply(SetupItem.SkillBus, Source, Target, Backup, Path.Combine(_files.Root, "core"));

        // Assert
        line.Should().Be(new SetupLine(SetupItem.SkillBus, SetupOutcome.Failed, "zyggy-core not available"));
    }

    [Fact]
    public void SameBytes_ExtraFileInTarget_Different()
    {
        // Arrange
        SeedSource();
        Install();
        Write(Path.Combine(Target, "extra.md"), "extra\n");

        // Act
        bool same = SkillInstaller.SameBytes(Source, Target);

        // Assert
        same.Should().BeFalse();
    }

    [Fact]
    public void SameBytes_OneByteDiffers_Different()
    {
        // Arrange
        SeedSource();
        Install();
        Write(Path.Combine(Target, "SKILL.md"), Body.Replace("fact", "Fact", StringComparison.Ordinal));

        // Act
        bool same = SkillInstaller.SameBytes(Source, Target);

        // Assert
        same.Should().BeFalse();
    }

    [Fact]
    public void SameBytes_IdenticalTrees_True()
    {
        // Arrange
        SeedSource();
        Install();
        Directory.CreateDirectory(Path.Combine(Target, "empty"));

        // Act
        bool same = SkillInstaller.SameBytes(Source, Target);

        // Assert
        same.Should().BeTrue();
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private SetupLine Install() => SkillInstaller.Apply(SetupItem.SkillRemember, Source, Target, Backup);

    private void SeedSource()
    {
        Write(Path.Combine(Source, "SKILL.md"), Body);
        Write(Path.Combine(Source, "refs", "notes.md"), "notes\n");
    }
}
