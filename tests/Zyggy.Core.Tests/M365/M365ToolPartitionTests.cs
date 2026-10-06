using Zyggy.Core.M365.Tools;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The pinned server's tool partition as template data (spec 33 AC-27, owner decision 8): read from
/// <c>.claude/skills/m365/tools/*.txt</c>, checked, and turned into the server filter and the three runs' allow and deny lists — today's
/// lists with each script rule replaced by its verb (hand-derived goldens).
/// </summary>
public sealed class M365ToolPartitionTests : IDisposable
{
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public M365ToolPartitionTests()
    {
        Directory.CreateDirectory(ToolsDirectory);
        foreach (var file in Directory.EnumerateFiles(M365Run.Golden("tools")))
        {
            File.Copy(file, Path.Combine(ToolsDirectory, Path.GetFileName(file)));
        }
    }

    private string ToolsDirectory => Path.Combine(_checkout, ".claude", "skills", "m365", "tools");

    public void Dispose() => Directory.Delete(_checkout, recursive: true);

    [Fact]
    public void Load_Golden_EnabledUnionExcludedEqualsServerList()
    {
        // Act
        var partition = Load();

        // Assert
        partition.Enabled.Concat(partition.Excluded).Order(StringComparer.Ordinal)
            .Should().Equal(File.ReadAllLines(M365Run.Golden("fixtures", "tools-0.157.2.txt")).Order(StringComparer.Ordinal));
        partition.Enabled.Should().HaveCount(16);
        partition.Excluded.Should().HaveCount(328);
        partition.ServerVersion.Should().Be("0.157.2");
    }

    [Fact]
    public void Load_Disjoint_SortedUnique()
    {
        // Act
        var partition = Load();

        // Assert
        partition.Enabled.Intersect(partition.Excluded).Should().BeEmpty();
        partition.Enabled.Should().BeInAscendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Load_ActionsSubsetAndAuthExcluded()
    {
        // Act
        var partition = Load();

        // Assert
        partition.Actions.Should().Equal("move-shared-mailbox-message", "send-shared-mailbox-mail", "upload-file-content");
        partition.Auth.Should().BeSubsetOf(partition.Excluded);
    }

    [Theory]
    [InlineData("brief-allow")]
    [InlineData("brief-deny")]
    [InlineData("mail-backfill-allow")]
    [InlineData("mail-backfill-deny")]
    [InlineData("files-backfill-allow")]
    [InlineData("files-backfill-deny")]
    public void RunLists_EqualHandDerivedGolden(string name)
    {
        // Arrange
        var partition = Load();
        var list = name switch
        {
            "brief-allow" => partition.BriefAllow,
            "brief-deny" => partition.BriefDeny,
            "mail-backfill-allow" => partition.MailBackfillAllow,
            "mail-backfill-deny" => partition.MailBackfillDeny,
            "files-backfill-allow" => partition.FilesBackfillAllow,
            _ => partition.FilesBackfillDeny,
        };

        // Assert
        list.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", name + ".txt")));
    }

    [Fact]
    public void RunLists_ActionToolsAlwaysDenied()
    {
        // Arrange
        var partition = Load();
        var actions = partition.Actions.Select(a => "mcp__m365__" + a).ToList();

        // Assert
        foreach (var (allow, deny) in new[] { (partition.BriefAllow, partition.BriefDeny), (partition.MailBackfillAllow, partition.MailBackfillDeny), (partition.FilesBackfillAllow, partition.FilesBackfillDeny) })
        {
            allow.Should().NotIntersectWith(actions);
            deny.Should().Contain(actions);
        }
    }

    [Fact]
    public void BriefDeny_EndsWithTheBriefAndMemoryVerbs()
    {
        // Arrange
        var partition = Load();

        // Assert
        partition.BriefDeny.TakeLast(2).Should().Equal("Bash(zyggy brief *)", "Bash(zyggy memory *)");
        partition.MailBackfillDeny.Should().NotContain("Bash(zyggy brief *)");
        partition.FilesBackfillDeny.Should().NotContain("Bash(zyggy brief *)");
    }

    [Fact]
    public void EnabledToolsRegex_AnchoredEqualsShell()
    {
        // Assert: ZY_M365_ENABLED_TOOLS of m365-lib.sh, copied by hand
        Load().EnabledToolsRegex.Should().Be(
            "^(create-shared-mailbox-draft|create-shared-mailbox-reply-draft|download-bytes-to-file|get-drive-delta|get-drive-item|get-drive-root-item|get-shared-mailbox-message|get-sharepoint-site-drive-by-id|list-drive-item-versions|list-folder-files|list-shared-mailbox-folder-messages|list-shared-mailbox-messages|list-sharepoint-site-drives|move-shared-mailbox-message|search-onedrive-files|send-shared-mailbox-mail)$");
    }

    [Theory]
    [InlineData("enabled.txt", "b-tool\na-tool\n", "is not sorted and unique")]
    [InlineData("enabled.txt", "a-tool\na-tool\n", "is not sorted and unique")]
    [InlineData("enabled.txt", "accept-calendar-event\n", "enabled and excluded overlap")]
    [InlineData("actions.txt", "no-such-tool\n", "actions.txt names a tool outside the partition")]
    [InlineData("auth.txt", "create-shared-mailbox-draft\n", "auth.txt names a tool that is not excluded")]
    [InlineData("enabled.txt", null, "is missing")]
    [InlineData("enabled.txt", "a-tool\r\n", "is not one name per line")]
    public void Load_BrokenFile_ExitThree(string file, string? content, string message)
    {
        // Arrange
        var path = Path.Combine(ToolsDirectory, file);
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, content);
        }

        // Act
        var load = M365ToolPartition.Load(_checkout);

        // Assert
        load.Partition.Should().BeNull();
        load.Error.Should().StartWith("configuration error: ").And.Contain(message);
    }

    [Fact]
    public void Binary_CarriesNoToolList()
    {
        // Assert: no embedded resource names a tool, and no source outside the guard names an action tool
        typeof(M365ToolPartition).Assembly.GetManifestResourceNames().Should().NotContain(n => n.Contains("tools", StringComparison.OrdinalIgnoreCase));
        var src = Path.Combine(RepoRoot(), "src");
        var holders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("list-shared-mailbox-messages", StringComparison.Ordinal))
            .ToList();
        holders.Should().BeEmpty();
    }

    private M365ToolPartition Load() => M365ToolPartition.Load(_checkout).Partition!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zyggy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
