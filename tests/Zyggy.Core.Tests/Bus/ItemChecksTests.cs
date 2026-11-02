using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;
using Zyggy.Core.Tests.Memory;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-12..AC-15 on the laptop: every check the archive would run that a laptop can run, before anything leaves it — the source
/// against the laptop deny list, the bytes read once, type and Office content, the laptop cap, secrets and contact details, the composed
/// index line — each with the archive's own words and never the matched text. No allowed-types, caps or slug check on the laptop.
/// </summary>
public sealed class ItemChecksTests : IDisposable
{
    private const string TextItem = "Restore the staging database from Sunday's snapshot.\nThen re-run the migrations.\n";

    private static readonly DateOnly Today = new(2026, 10, 31);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut-items", Guid.NewGuid().ToString("N"));

    public ItemChecksTests() => Directory.CreateDirectory(Sources);

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string Home => Path.Combine(_root, "home");

    private string Sources => Path.Combine(Home, "Downloads");

    private string Xdg => Path.Combine(_root, "xdg");

    private string Credentials => Path.Combine(_root, "creds");

    private string NodeJsonPath => Path.Combine(_root, "config", "node.json");

    private string NodeDir => Path.Combine(_root, "node");

    private string BusLocalPath => Path.Combine(_root, "bus");

    private string SecretsRoot => Path.Combine(_root, "secrets");

    private string OneDriveFolder => Path.Combine(_root, "OneDrive - Example", "Zyggy", "archive");

    public static TheoryData<string, string> SourceRefusals() => new()
    {
        { "ssh", "denied_location" },
        { "config-zyggy", "denied_location" },
        { "state-zyggy", "denied_location" },
        { "share-zyggy", "denied_location" },
        { "xdg-zyggy", "denied_location" },
        { "node-json-folder", "denied_location" },
        { "node-dir", "denied_location" },
        { "bus-local-path", "denied_location" },
        { "secrets-root", "denied_location" },
        { "credentials", "denied_location" },
        { "dot-claude", "denied_location" },
        { "onedrive-folder", "denied_location" },
        { "missing", "not_found" },
        { "directory", "not_regular_file" },
        { "not-normalised", "symlink" },
    };

    public static TheoryData<string, string, string> SecretPlacements()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var row in SecretPatternsTests.SecretSamples())
        {
            var (name, sample) = row.Data;
            data.Add(name, sample, "line");
            data.Add(name, sample, "name");
            data.Add(name, sample, "description");
        }

        return data;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(SourceRefusals))]
    public void Check_SourceRefused_DetailPerLocation(string location, string detail)
    {
        // Arrange
        var source = SourceAt(location);

        // Act
        var result = Check(Request(source));

        // Assert
        result.Candidate.Should().BeNull();
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be(detail);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links without privileges: Linux only")]
    public void Check_SymlinkedParent_OnLinux_SourceRefusedSymlink()
    {
        // Arrange
        Write(Path.Combine(Home, "real", "notes.txt"), TextItem);
        var link = Path.Combine(Sources, "linked");
        Directory.CreateSymbolicLink(link, Path.Combine(Home, "real"));

        // Act
        var result = Check(Request(Path.Combine(link, "notes.txt")));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("symlink");
    }

    [Fact]
    public void Check_UploadsUnderDotClaude_Allowed()
    {
        // Arrange
        var source = Write(Path.Combine(Home, ".claude", "uploads", "session-1", "restore-notes.txt"), TextItem);

        // Act
        var result = Check(Request(source));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate!.SourceName.Should().Be("restore-notes.txt");
    }

    public static TheoryData<string, string, string?> Refusals() => new()
    {
        { "empty", "empty", null },
        { "random", "type_refused", null },
        { "docm", "office_refused", "macro_enabled" },
        { "over-cap", "too_large", "10485761 > 10485760" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Check_Empty_TypeRefused_OfficeRefused_TooLarge(string content, string refusal, string? detail)
    {
        // Arrange
        var source = content switch
        {
            "empty" => Write(Path.Combine(Sources, "empty.txt"), []),
            "random" => Write(Path.Combine(Sources, "blob.bin"), [0x00, 0xFF, 0x13, 0x37, 0x00, 0x01, 0xFE, 0x80]),
            "docm" => Write(Path.Combine(Sources, "report.docx"), File.ReadAllBytes(Path.Combine(Golden.Directory, "office", "sample.docm"))),
            _ => Write(Path.Combine(Sources, "big.txt"), Enumerable.Repeat((byte)'a', 10_485_761).ToArray()),
        };

        // Act
        var result = Check(Request(source));

        // Assert
        result.Candidate.Should().BeNull();
        ArchiveRefusalWire.ToWire(result.Refusal!.Value).Should().Be(refusal);
        result.Detail.Should().Be(detail);
    }

    [Fact]
    public void Check_ItemMaxBytesSetLower_TooLargeAgainstIt()
    {
        // Arrange
        var source = Write(Path.Combine(Sources, "notes.txt"), "eleven char");

        // Act
        var result = Check(Request(source), new ArchiveSettings { ItemMaxBytes = 10 });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TooLarge);
        result.Detail.Should().Be("11 > 10");
    }

    [Theory]
    [MemberData(nameof(SecretPlacements))]
    public void Check_SecretLine_Name_Description_RefusedByPatternName_TextNowhere(string pattern, string sample, string where)
    {
        // Arrange
        var source = Write(Path.Combine(Sources, "notes.txt"), where == "line" ? "First line\n" + sample + "\n" : TextItem);
        var request = Request(source, name: where == "name" ? sample : "Staging restore notes", description: where == "description" ? sample : "How the staging database was restored");

        // Act
        var result = Check(request);

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        result.Detail.Should().Be(pattern + (where == "line" ? " (line 2)" : $" ({where})"));
        result.Detail.Should().NotContain(sample);
    }

    [Fact]
    public void Check_ContactDetailInName_ContactDetailName()
    {
        // Arrange
        var source = Write(Path.Combine(Sources, "notes.txt"), TextItem);

        // Act
        var result = Check(Request(source, name: "Notes for alice@acme.example"));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.ContactDetail);
        result.Detail.Should().Be("name");
    }

    [Fact]
    public void Check_ComposedIndexLineOver400_UsageMessage()
    {
        // Arrange
        var source = Write(Path.Combine(Sources, "notes.txt"), TextItem);
        var name = string.Concat(Enumerable.Repeat("Staging restore notes ", 5))[..100];
        var description = string.Concat(Enumerable.Repeat("How the staging database was restored ", 4))[..149];
        var request = new ItemRequest(Slug.Parse("calizr-staging-environment-and-the-restore-of-its-database"), name, description, source, Slug.Parse("staging-restore-notes-for-the-database-of-the-calizr-project"), viaBus: false);

        // Act
        var result = Check(request);

        // Assert
        result.Candidate.Should().BeNull();
        result.Refusal.Should().BeNull();
        result.UsageError.Should().StartWith("the index line would exceed 400 characters (").And.EndWith("): shorten --name or --description");
    }

    [Fact]
    public void Check_MarkdownSource_TypeTextMarkdown_DriveNameTxt()
    {
        // Arrange
        var source = Write(Path.Combine(Sources, "restore-notes.md"), "# Staging restore\n\nRestore from Sunday's snapshot.\n");

        // Act
        var result = Check(Request(source));

        // Assert
        var candidate = result.Candidate!;
        candidate.Type.Should().Be(ArchiveMediaType.TextMarkdown);
        candidate.DriveName.Should().Be("staging-restore-notes.txt");
        candidate.SourceName.Should().Be("restore-notes.md");
        candidate.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Check_NoAllowedTypesNoCapsNoSlugCheck_OnTheLaptop()
    {
        // Arrange
        var png = ImageBytes.Png(2, 2);
        var source = Write(Path.Combine(Sources, "diagram.png"), png);

        // Act
        var result = Check(Request(source));

        // Assert
        var candidate = result.Candidate!;
        candidate.Type.Should().Be(ArchiveMediaType.Png);
        candidate.Bytes.Should().Equal(png);
        candidate.DriveName.Should().Be("staging-restore-notes.png");
    }

    private static string Write(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ItemRequest Request(string source, string name = "Staging restore notes", string description = "How the staging database was restored") =>
        new(Slug.Parse("calizr"), name, description, source, Slug.Parse("staging-restore-notes"), viaBus: false);

    private string SourceAt(string location) => location switch
    {
        "ssh" => Path.Combine(Home, ".ssh", "id_ed25519"),
        "config-zyggy" => Path.Combine(Home, ".config", "zyggy", "x.txt"),
        "state-zyggy" => Path.Combine(Home, ".local", "state", "zyggy", "x.txt"),
        "share-zyggy" => Path.Combine(Home, ".local", "share", "zyggy", "x.txt"),
        "xdg-zyggy" => Path.Combine(Xdg, "zyggy", "x.txt"),
        "node-json-folder" => Path.Combine(Path.GetDirectoryName(NodeJsonPath)!, "x.txt"),
        "node-dir" => Path.Combine(NodeDir, "runs", "x.txt"),
        "bus-local-path" => Path.Combine(BusLocalPath, "tenants", "acme", "x.md"),
        "secrets-root" => Path.Combine(SecretsRoot, "acme", "hmac-1"),
        "credentials" => Path.Combine(Credentials, "graph.pfx"),
        "dot-claude" => Path.Combine(Home, ".claude", ".credentials.json"),
        "onedrive-folder" => Path.Combine(OneDriveFolder, "calizr", "x.txt"),
        "missing" => Path.Combine(Sources, "missing.txt"),
        "directory" => Sources,
        _ => Write(Path.Combine(Sources, "notes.txt"), TextItem).Replace("Downloads", Path.Combine("Downloads", "."), StringComparison.Ordinal),
    };

    private ItemCheckResult Check(ItemRequest request, ArchiveSettings? settings = null)
    {
        var node = Node(settings ?? new ArchiveSettings { OneDriveFolder = OneDriveFolder });
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["XDG_CONFIG_HOME"] = Xdg,
            ["CREDENTIALS_DIRECTORY"] = Credentials,
        };
        var deny = SourceDenyList.ForNode(env, node, NodeJsonPath);
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return ItemChecks.Check(request, node.Archive, deny, patterns, Today);
    }

    private NodeConfiguration Node(ArchiveSettings archive) =>
        new(
            TenantId.Parse("acme"),
            MachineName.Parse("work-laptop"),
            KeyId.Parse("acme/1"),
            new BusSettings("acme-org", "acme-bus", "main", BusLocalPath, "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(),
            new SecretsSettings(SecretStoreKind.File, SecretsRoot))
        {
            NodeDir = NodeDir,
            Archive = archive,
        };
}
