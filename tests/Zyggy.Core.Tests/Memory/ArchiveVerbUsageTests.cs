using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// <c>zyggy memory archive</c> before any read of the source (spec 37 AC-4, AC-5, AC-6): the unattended refusal, configuration errors
/// naming their key, usage errors as one line, and the index-line length check — nothing read, nothing written.
/// </summary>
public sealed class ArchiveVerbUsageTests : IDisposable
{
    private const string UsageAdd =
        " (usage: zyggy memory archive add --project <slug> --name <text> --description <text> --file <absolute path> [--slug <slug>])\n";

    private readonly MemoryTree _tree = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public ArchiveVerbUsageTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
    }

    public static TheoryData<string, string?, string> ConfigErrors() => new()
    {
        { "ZYGGY_MEMORY_ROOT", null, "archive: configuration error: ZYGGY_MEMORY_ROOT is not set\n" },
        { "ZYGGY_TENANT", null, "archive: configuration error: ZYGGY_TENANT is not set\n" },
        { "ZYGGY_USER", null, "archive: configuration error: ZYGGY_USER is not set\n" },
        { "ZYGGY_SECRET_PATTERNS", null, "archive: configuration error: ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)\n" },
        { "ZYGGY_TIMEZONE", "Mars/Olympus_Mons", "archive: configuration error: ZYGGY_TIMEZONE 'Mars/Olympus_Mons' is not a known time zone\n" },
        { "ZYGGY_ARCHIVE_CONFIG", "<config>", "archive: configuration error: item_max_bytes" },
    };

    public static TheoryData<string[], string> UsageErrors() => new()
    {
        { ["add"], "--project is required" },
        { ["add", "--project", "zyggy", "--name", "Quote", "--description", "Roof", "--file", "relative/x.pdf"], "--file must be an absolute path" },
        { ["add", "--project", "zyggy", "--name", "Quote", "--description", "Roof", "--file", MissingSource, "--bogus"], "unexpected argument '--bogus'" },
    };

    private static string MissingSource => Path.Combine(Path.GetTempPath(), "zyggy-ut-archive-missing", "no-such-file.txt");

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Run_HooksOff_Add_ExitTwoUnattendedNothingRead()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";
        _environment.Remove("ZYGGY_MEMORY_ROOT");

        // Act
        var (exit, console) = await RunAsync(ValidAdd());

        // Assert
        exit.Should().Be(2);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("archive: refused: unattended run\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_HooksOff_Remove_ExitTwoUnattended()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync("remove", "zyggy/quote");

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("archive: refused: unattended run\n");
    }

    [Fact]
    public async Task Run_HooksOff_List_IsNotRefused()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync("list");

        // Assert
        exit.Should().NotBe(2);
        console.Stderr.Should().NotContain("unattended");
    }

    [Theory]
    [MemberData(nameof(ConfigErrors))]
    public async Task Run_ConfigMissing_ExitThreeNamesKey(string key, string? value, string stderrStart)
    {
        // Arrange
        if (value is null)
        {
            _environment.Remove(key);
        }
        else if (value == "<config>")
        {
            var config = Path.Combine(_tree.Root, "archive.json");
            await File.WriteAllTextAsync(config, """{"item_max_bytes": 99999999999}""", TestContext.Current.CancellationToken);
            _environment[key] = config;
        }
        else
        {
            _environment[key] = value;
        }

        // Act
        var (exit, console) = await RunAsync(ValidAdd());

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().StartWith(stderrStart);
        console.Stderr.Count(c => c == '\n').Should().Be(1);
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Run_UsageError_ExitFourOneLineNamingVerb(string[] args, string message)
    {
        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("archive: " + message + UsageAdd);
    }

    [Fact]
    public async Task Run_IndexLineOver400_ExitFourBeforeAnyRead()
    {
        // Arrange
        var project = new string('p', 60);
        var slug = new string('s', 60);
        var name = string.Concat(Enumerable.Repeat("name ", 20)).TrimEnd();
        var description = string.Concat(Enumerable.Repeat("word ", 30))[..149].TrimEnd();

        // Act
        var (exit, console) = await RunAsync("add", "--project", project, "--name", name, "--description", description, "--file", MissingSource, "--slug", slug);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().StartWith("archive: the index line would exceed 400 characters (");
        console.Stderr.Should().EndWith("): shorten --name or --description\n");
        Directory.EnumerateFileSystemEntries(_tree.PrincipalDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_UnknownSubVerb_ExitFour()
    {
        // Act
        var (exit, console) = await RunAsync("show", "zyggy/quote");

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("archive: unknown verb 'show' (usage: zyggy memory archive add|list|remove ...)\n");
    }

    private static string[] ValidAdd() =>
        ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", "Roof repair quote", "--file", MissingSource];

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var verb = new ArchiveVerb(_environment, _clock, FindTimeZone);
        var exit = await verb.RunAsync(args, console.Io, CancellationToken.None);
        return (exit, console);
    }

    // Europe/Brussels in late September is CEST (+02:00); IANA ids do not resolve off Linux in an invariant-globalization build.
    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}
