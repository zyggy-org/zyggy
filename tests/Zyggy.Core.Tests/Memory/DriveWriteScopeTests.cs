using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-6: a write is sent only as a project folder under the root or a new item file inside one; anything else names its rule.</summary>
public sealed class DriveWriteScopeTests
{
    private const string Drive = "b!onedrive0001";
    private const string Root = "01ROOTARCH";
    private const string Project = "01PROJZYGGY";

    private static readonly string[] Rules =
    [
        "other_drive", "replace_form", "conflict_behaviour", "too_large", "folder_parent_not_root", "folder_name_not_slug",
        "file_parent_not_project_folder", "file_name", "extension",
    ];

    public static TheoryData<string, string, string, string, string, string?, long> Refusals() => new()
    {
        { "other_drive", nameof(DriveWriteKind.CreateFile), "b!other0001", Project, "quote-2026.txt", "fail", 68 },
        { "folder_parent_not_root", nameof(DriveWriteKind.CreateFolder), Drive, Project, "nested", "fail", 0 },
        { "file_parent_not_project_folder", nameof(DriveWriteKind.CreateFile), Drive, Root, "quote-2026.txt", "fail", 68 },
        { "file_parent_not_project_folder", nameof(DriveWriteKind.CreateFile), Drive, "01UNKNOWN", "quote-2026.txt", "fail", 68 },
        { "folder_name_not_slug", nameof(DriveWriteKind.CreateFolder), Drive, Root, "Bad Slug", "fail", 0 },
        { "folder_name_not_slug", nameof(DriveWriteKind.CreateFolder), Drive, Root, "..", "fail", 0 },
        { "folder_name_not_slug", nameof(DriveWriteKind.CreateFolder), Drive, Root, "a/b", "fail", 0 },
        { "folder_name_not_slug", nameof(DriveWriteKind.CreateFolder), Drive, Root, "zyggy\n", "fail", 0 },
        { "file_name", nameof(DriveWriteKind.CreateFile), Drive, Project, "../x.txt", "fail", 68 },
        { "file_name", nameof(DriveWriteKind.CreateFile), Drive, Project, "a/b.txt", "fail", 68 },
        { "file_name", nameof(DriveWriteKind.CreateFile), Drive, Project, "x\u0007.txt", "fail", 68 },
        { "file_name", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote.txt.", "fail", 68 },
        { "extension", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote.docx", "fail", 68 },
        { "extension", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote.PNG", "fail", 68 },
        { "extension", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote.md", "fail", 68 },
        { "replace_form", nameof(DriveWriteKind.ReplaceById), Drive, Project, "quote-2026.txt", "fail", 68 },
        { "conflict_behaviour", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote-2026.txt", "replace", 68 },
        { "conflict_behaviour", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote-2026.txt", "rename", 68 },
        { "conflict_behaviour", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote-2026.txt", null, 68 },
        { "too_large", nameof(DriveWriteKind.CreateFile), Drive, Project, "quote-2026.txt", "fail", 250_000_001 },
    };

    [Fact]
    public void Check_ValidFolderUnderRoot_Null()
    {
        // Act
        var rule = Scope().Check(new DriveWriteRequest(DriveWriteKind.CreateFolder, Drive, Root, "zyggy", "fail", 0));

        // Assert
        rule.Should().BeNull();
    }

    [Fact]
    public void Check_ValidFileInKnownProjectFolder_Null()
    {
        // Act
        var rule = Scope().Check(new DriveWriteRequest(DriveWriteKind.CreateFile, Drive, Project, "quote-2026.txt", "fail", 250_000_000));

        // Assert
        rule.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Check_Refused_NamesRule(string expected, string kind, string drive, string parent, string name, string? conflict, long size)
    {
        // Act
        var rule = Scope().Check(new DriveWriteRequest(Enum.Parse<DriveWriteKind>(kind), drive, parent, name, conflict, size));

        // Assert
        rule.Should().Be(expected);
    }

    [Fact]
    public void Check_RootRegisteredAsProjectFolder_FileInRootStillRefused()
    {
        // Arrange
        var scope = Scope();
        scope.KnowProjectFolder(Root);

        // Act
        var rule = scope.Check(new DriveWriteRequest(DriveWriteKind.CreateFile, Drive, Root, "quote-2026.txt", "fail", 68));

        // Assert
        rule.Should().Be("file_parent_not_project_folder");
    }

    [Fact]
    public void EveryRule_HasAProducingRow()
    {
        // Act
        var produced = Refusals().Select(row => row.Data.Item1).Distinct().Order(StringComparer.Ordinal);

        // Assert
        produced.Should().Equal(Rules.Order(StringComparer.Ordinal));
    }

    private static DriveWriteScope Scope()
    {
        var scope = new DriveWriteScope(Drive, Root);
        scope.KnowProjectFolder(Project);
        return scope;
    }
}
