using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// A refused <c>archive add</c> (spec 37 AC-12): exit 2, stdout empty, exactly one stderr line <c>archive: refused: &lt;reason&gt;[ (&lt;detail&gt;)]</c>,
/// the memory tree byte-for-byte unchanged, no inbox, no item text echoed.
/// </summary>
public sealed class ArchiveVerbRefusalTests : IDisposable
{
    private const string Secret = "AKIAABCDEFGHIJKLMNOP";

    private readonly MemoryTree _tree = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly string _home;
    private readonly Dictionary<string, string?> _environment;

    public ArchiveVerbRefusalTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-refusal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = _home,
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        _tree.Write("business/areas/_index.md", "---\nname: areas\ndescription: areas\nupdated: 2026-09-01\n---\n");
    }

    public static TheoryData<string, string> Refusals() => new()
    {
        { "not_found", "archive: refused: source_refused (not_found)\n" },
        { "type_refused", "archive: refused: type_refused\n" },
        { "too_large", "archive: refused: too_large (68 > 10)\n" },
        { "secret_pattern", "archive: refused: secret_pattern aws-access-key (line 2)\n" },
        { "contact_detail", "archive: refused: contact_detail (description)\n" },
        { "slug_taken", "archive: refused: slug_taken\n" },
        { "office:sample.docm", "archive: refused: office_refused (macro_enabled)\n" },
        { "office:sample.doc", "archive: refused: office_refused (legacy_binary)\n" },
        { "office:sample.odt", "archive: refused: office_refused (opendocument)\n" },
        { "office:plain.zip", "archive: refused: office_refused (not_office)\n" },
        { "type_not_allowed", "archive: refused: type_not_allowed (application/vnd.openxmlformats-officedocument.wordprocessingml.document)\n" },
    };

    [Theory]
    [InlineData("office:sample.docm")]
    [InlineData("office:sample.doc")]
    [InlineData("office:sample.odt")]
    [InlineData("office:plain.zip")]
    [InlineData("type_not_allowed")]
    public async Task Run_RefusedOffice_StderrNeverNamesPartOrContentType(string scenario)
    {
        // Arrange
        var args = await ArrangeAsync(scenario);

        // Act
        var (exit, console) = await RunAsync(args);

        // Assert: the detail word only — never a part name or a content type beyond type_not_allowed's wire string.
        exit.Should().Be(2);
        console.Stderr.Should().NotContainAny(".xml", "+xml", "vbaProject", "mimetype", "[Content_Types]");
        if (scenario != "type_not_allowed")
        {
            console.Stderr.Should().NotContain("/vnd.");
        }
    }

    public void Dispose()
    {
        _tree.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Run_Refusal_ExitTwoOneStderrLineNothingWritten(string scenario, string stderr)
    {
        // Arrange
        var args = await ArrangeAsync(scenario);
        var before = Disk();

        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(2);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be(stderr);
        Disk().Should().Equal(before, "nothing reaches the memory tree");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_RefusedText_NeverEchoesContent()
    {
        // Arrange
        var args = await ArrangeAsync("secret_pattern");

        // Act
        var (_, console) = await RunAsync(args);

        // Assert
        console.Stderr.Should().NotContainAny(Secret, "Meeting notes", "Next steps");
    }

    private async Task<string[]> ArrangeAsync(string scenario)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_home, "Downloads", "quote.txt");
        var text = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";
        var description = "Roof repair quote";
        switch (scenario)
        {
            case "not_found":
                break;
            case "type_refused":
                await File.WriteAllBytesAsync(source, [0x00, 0x01, 0x02, 0xFE, 0xFF], token);
                break;
            case "too_large":
                await File.WriteAllBytesAsync(source, new UTF8Encoding(false).GetBytes(text), token);
                var config = Path.Combine(_home, "archive.json");
                await File.WriteAllTextAsync(config, """{"item_max_bytes": 10, "project_max_bytes": 100, "total_max_bytes": 1000}""", token);
                _environment["ZYGGY_ARCHIVE_CONFIG"] = config;
                break;
            case "secret_pattern":
                await File.WriteAllBytesAsync(source, new UTF8Encoding(false).GetBytes($"Meeting notes.\nkey {Secret}\nNext steps.\n"), token);
                break;
            case "contact_detail":
                await File.WriteAllBytesAsync(source, new UTF8Encoding(false).GetBytes(text), token);
                description = "Call +32 470 12 34 56";
                break;
            case "slug_taken":
                await File.WriteAllBytesAsync(source, new UTF8Encoding(false).GetBytes(text), token);
                _tree.Write("archive/zyggy/quote-2026.md", "---\nname: Quote 2026\n---\n");
                break;
            case "type_not_allowed":
                // The instance's six-only archive.json (spec 39 AC-3): Office types stay off until the owner widens the list.
                File.Copy(Path.Combine(Golden.Directory, "office", "sample.docx"), source, overwrite: true);
                var sixOnly = Path.Combine(_home, "archive.json");
                await File.WriteAllTextAsync(sixOnly, """{"allowed_types": ["text/plain", "text/markdown", "application/pdf", "image/png", "image/jpeg", "image/gif"]}""", token);
                _environment["ZYGGY_ARCHIVE_CONFIG"] = sixOnly;
                break;
            case var office when office.StartsWith("office:", StringComparison.Ordinal):
                File.Copy(Path.Combine(Golden.Directory, "office", office["office:".Length..]), source, overwrite: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", description, "--file", source];
    }

    private Dictionary<string, string> Disk() =>
        Directory.EnumerateFiles(_tree.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)), StringComparer.Ordinal);

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string[] args)
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, new RecordingProcessRunner()).RunAsync(args, console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}
