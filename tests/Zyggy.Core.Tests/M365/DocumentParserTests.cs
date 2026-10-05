using System.Runtime.Versioning;
using System.Text;

using Zyggy.Core.M365;
using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// A downloaded document becomes bounded, secret-checked text (the template's <c>parse.sh</c>, spec 33 AC-29): the seven <c>parse:</c>
/// bats cases at unit level, MarkItDown faked through <see cref="IProcessRunner"/>.
/// </summary>
public sealed class DocumentParserTests : IDisposable
{
    private const string Prlimit = "/usr/bin/prlimit";
    private const string Markitdown = "/home/zyggy/.local/bin/markitdown";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly RecordingProcessRunner _runner = new();
    private readonly Dictionary<string, string?> _programs = new(StringComparer.Ordinal) { ["markitdown"] = Markitdown, ["prlimit"] = Prlimit };
    private M365ConfigurationLoad _configuration;

    public DocumentParserTests()
    {
        Directory.CreateDirectory(RunDirectory);
        var config = Path.Combine(_root, "m365.json");
        File.Copy(M365Run.Golden("fixtures", "m365.json"), config);
        _configuration = M365Configuration.Load(config, baseOnly: false, new DateOnly(2026, 9, 30), _ => true);
        _runner.Hook = spec => MarkitdownStub(spec);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string RunDirectory => Path.Combine(_root, "run");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Parse_ReportDocx_SecretLineWithheldStderrSummaryInputDeleted()
    {
        // Arrange
        var input = Input("report.docx");
        var expected = File.ReadAllText(M365Run.Golden("fixtures", "parsed-report.docx.txt"))
            .Replace("Access for the reporting tool: password: hunter2secret", "[line withheld: matches secret pattern credential-assignment]", StringComparison.Ordinal)
            .Replace("\a", string.Empty, StringComparison.Ordinal);

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(0, result.Stderr);
        Encoding.UTF8.GetString(result.Stdout).Should().Be(expected);
        result.Stderr.Should().Be("parse: report.docx 29 lines, 1 withheld\n");
        (Encoding.UTF8.GetString(result.Stdout) + result.Stderr).Should().NotContain("hunter2secret");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_BigPdf_CutAt20000WithMarker()
    {
        // Arrange
        var input = Input("big.pdf");
        var text = File.ReadAllBytes(M365Run.Golden("fixtures", "parsed-big.pdf.txt"));
        byte[] expected = [.. text.AsSpan(0, 20000), .. "\n[cut at 20000 bytes]\n"u8];

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(0, result.Stderr);
        result.Stdout.Should().Equal(expected);
        result.Stderr.Should().Be("parse: big.pdf 241 lines, 0 withheld, cut at 20000 bytes\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_CutRightAfterNewline_NoExtraNewline()
    {
        // Arrange: lines of 10 bytes, a cap of 20000 falls exactly after a newline
        _runner.Hook = _ => RecordingProcessRunner.Ok(string.Concat(Enumerable.Repeat("123456789\n", 2500)));

        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        Encoding.UTF8.GetString(result.Stdout).Should().EndWith("123456789\n[cut at 20000 bytes]\n");
        result.Stdout.Length.Should().Be(20000 + "[cut at 20000 bytes]\n".Length);
    }

    [Fact]
    public async Task Parse_Arguments_PrlimitAsThenMarkitdownThenFile()
    {
        // Arrange
        var input = Input("report.docx");

        // Act
        await ParseAsync(input);

        // Assert
        var spec = _runner.Calls.Should().ContainSingle().Subject;
        spec.FileName.Should().Be(Prlimit);
        spec.Arguments.Should().Equal("--as=2147483648", "--", Markitdown, input);
        spec.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        spec.StandardInput.Should().BeNull();
    }

    [Fact]
    public async Task Parse_OutsideRunDirectory_RefusedFiveLeftAlone()
    {
        // Arrange
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "report.docx");
        File.WriteAllText(file, "keep me");

        // Act
        var direct = await ParseAsync(file);
        var dotDot = await ParseAsync(Path.Combine(RunDirectory, "..", "outside", "report.docx"));
        var relative = await ParseAsync(Path.Combine("..", "outside", "report.docx"));

        // Assert
        foreach (var result in new[] { direct, dotDot, relative })
        {
            result.Exit.Should().Be(5);
            result.Stdout.Should().BeEmpty();
            result.Stderr.Should().Be("parse: refused: not in the run directory\n");
        }

        File.ReadAllText(file).Should().Be("keep me");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links without privileges: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Parse_SymlinksLeavingRunDirectory_RefusedLinkDeletedTargetKept()
    {
        // Arrange
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "report.docx");
        File.WriteAllText(file, "keep me");
        var link = Path.Combine(RunDirectory, "link.docx");
        File.CreateSymbolicLink(link, Path.Combine("..", "outside", "report.docx"));
        var sub = Path.Combine(RunDirectory, "sub");
        Directory.CreateSymbolicLink(sub, Path.Combine("..", "outside"));

        // Act
        var throughLink = await ParseAsync(link);
        var throughDirectory = await ParseAsync(Path.Combine(sub, "report.docx"));

        // Assert
        throughLink.Exit.Should().Be(5);
        throughLink.Stderr.Should().Be("parse: refused: not in the run directory\n");
        throughDirectory.Exit.Should().Be(5);
        new FileInfo(link).Exists.Should().BeFalse();
        File.ReadAllText(file).Should().Be("keep me");
        new DirectoryInfo(sub).LinkTarget.Should().NotBeNull();
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Parse_MissingFile_RefusedNotARegularFile()
    {
        // Act
        var result = await ParseAsync(Path.Combine(RunDirectory, "missing.docx"));

        // Assert
        result.Exit.Should().Be(5);
        result.Stderr.Should().Be("parse: refused: missing.docx is not a regular file\n");
    }

    [Fact]
    public async Task Parse_OverMaxBytes_RefusedNamedWithLimit()
    {
        // Arrange
        var input = Path.Combine(RunDirectory, "huge.pdf");
        using (var stream = File.Create(input))
        {
            stream.SetLength(15728641);
        }

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(5);
        result.Stderr.Should().Be("parse: refused: huge.pdf is 15728641 bytes (limit 15728640)\n");
        File.Exists(input).Should().BeFalse();
        _runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("file.exe", "type .exe not parsable")]
    [InlineData("file.zip", "type .zip not parsable")]
    [InlineData("file.jpg", "type .jpg not parsable")]
    [InlineData("README", "type (none) not parsable")]
    public async Task Parse_TypeNotParsable_Refused(string name, string message)
    {
        // Arrange
        var input = Input(name);

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(5);
        result.Stderr.Should().Be($"parse: refused: {message}\n");
        File.Exists(input).Should().BeFalse();
        _runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    [InlineData("pdf")]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("html")]
    [InlineData("htm")]
    [InlineData("DOCX")]
    [InlineData("Pdf")]
    public async Task Parse_EveryAllowedTypeAnyCase_ReachesMarkitdown(string extension)
    {
        // Arrange
        var input = Input("unknown." + extension);

        // Act
        var result = await ParseAsync(input);

        // Assert: the stub has no text for this name, so the type passed and MarkItDown failed
        result.Exit.Should().Be(6);
        result.Stderr.Should().StartWith("parse: markitdown failed (");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_MarkitdownFails_ExitSixFirstErrorLine()
    {
        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        result.Exit.Should().Be(6);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Be("parse: markitdown failed (markitdown: UnsupportedFormatException: could not convert notes.txt)\n");
    }

    [Fact]
    public async Task Parse_MarkitdownErrorMatchesSecret_Withheld()
    {
        // Arrange
        _runner.Hook = _ => RecordingProcessRunner.Fail(1, "error: password: hunter2secret\nsecond line\n");

        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        result.Exit.Should().Be(6);
        result.Stderr.Should().Be("parse: markitdown failed (first error line withheld: matches secret pattern credential-assignment)\n");
    }

    [Fact]
    public async Task Parse_MarkitdownErrorLong_CutTo200Characters()
    {
        // Arrange
        var words = string.Concat(Enumerable.Repeat("word ", 60));
        _runner.Hook = _ => RecordingProcessRunner.Fail(1, "e\t" + words + "\n");

        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        result.Stderr.Should().Be("parse: markitdown failed (" + ("e " + words)[..200] + ")\n");
    }

    [Fact]
    public async Task Parse_Timeout_ExitSix()
    {
        // Arrange
        _runner.Hook = _ => new ProcessResult(null, "partial", "", TimedOut: true, StartFailed: false, StdoutTruncated: false, TimeSpan.FromSeconds(120));
        var input = Input("report.docx");

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(6);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Be("parse: markitdown timed out after 120 s\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_MarkitdownMissing_ExitThreeNamesRunbookInputDeleted()
    {
        // Arrange
        _programs["markitdown"] = null;
        var input = Input("report.docx");

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(3);
        result.Stderr.Should().Be("parse: markitdown not found — runbook 13 \"MarkItDown\"\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_PrlimitMissing_ExitThree()
    {
        // Arrange
        _programs["prlimit"] = null;

        // Act
        var result = await ParseAsync(Input("report.docx"));

        // Assert
        result.Exit.Should().Be(3);
        result.Stderr.Should().Be("parse: prlimit not found\n");
    }

    [Fact]
    public async Task Parse_ConfigurationInvalid_ExitThreeInputDeleted()
    {
        // Arrange
        _configuration = new M365ConfigurationLoad(null, "configuration error: /x/m365.json is not valid JSON", null);
        var input = Input("report.docx");

        // Act
        var result = await ParseAsync(input);

        // Assert
        result.Exit.Should().Be(3);
        result.Stderr.Should().Be("parse: configuration error: /x/m365.json is not valid JSON\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_RunDirUnset_ExitThreeInputKept()
    {
        // Arrange
        var input = Input("report.docx");

        // Act
        var result = await Parser().ParseAsync(new ParseRequest(input, null, _root), CancellationToken.None);

        // Assert
        result.Exit.Should().Be(3);
        result.Stderr.Should().Be("parse: configuration error: ZYGGY_M365_RUN_DIR is not set\n");
        File.Exists(input).Should().BeTrue();
    }

    [Fact]
    public async Task Parse_RunDirNotADirectory_ExitThree()
    {
        // Arrange
        var nope = Path.Combine(_root, "nope");

        // Act
        var result = await Parser().ParseAsync(new ParseRequest(Path.Combine(nope, "report.docx"), nope, _root), CancellationToken.None);

        // Assert
        result.Exit.Should().Be(3);
        result.Stderr.Should().Be($"parse: configuration error: ZYGGY_M365_RUN_DIR {nope} is not a directory\n");
    }

    [Fact]
    public async Task Parse_ControlCharactersAndCarriageReturnsRemovedTabsKept()
    {
        // Arrange
        _runner.Hook = _ => RecordingProcessRunner.Ok("a\tb\r\nc\u0001d\u000ce\u007f\n");

        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        Encoding.UTF8.GetString(result.Stdout).Should().Be("a\tb\ncde\n");
        result.Stderr.Should().Be("parse: notes.txt 2 lines, 0 withheld\n");
    }

    [Fact]
    public async Task Parse_EmptyText_ZeroLines()
    {
        // Arrange
        _runner.Hook = _ => RecordingProcessRunner.Ok(string.Empty);

        // Act
        var result = await ParseAsync(Input("notes.txt"));

        // Assert
        result.Exit.Should().Be(0);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Be("parse: notes.txt 0 lines, 0 withheld\n");
    }

    [Fact]
    public async Task Parse_CertificateWarning_PrintedBeforeSummary()
    {
        // Arrange
        _configuration = _configuration with { Warning = "m365: certificate expires in 20 days — runbook 13 \"Rotate the certificate\"" };

        // Act
        var result = await ParseAsync(Input("report.docx"));

        // Assert
        result.Stderr.Should().Be("m365: certificate expires in 20 days — runbook 13 \"Rotate the certificate\"\nparse: report.docx 29 lines, 1 withheld\n");
    }

    private static ProcessResult MarkitdownStub(ProcessSpec spec)
    {
        var name = Path.GetFileName(spec.Arguments[^1]);
        var text = M365Run.Golden("fixtures", $"parsed-{name}.txt");
        return File.Exists(text)
            ? RecordingProcessRunner.Ok(Encoding.UTF8.GetString(File.ReadAllBytes(text)))
            : RecordingProcessRunner.Fail(1, $"markitdown: UnsupportedFormatException: could not convert {name}\n");
    }

    private string Input(string name)
    {
        var path = Path.Combine(RunDirectory, name);
        File.WriteAllText(path, "PK fake bytes\n");
        return path;
    }

    private DocumentParser Parser() => new(
        _runner,
        () => _configuration,
        name => _programs.GetValueOrDefault(name),
        () => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")));

    private Task<ParseResult> ParseAsync(string input) => Parser().ParseAsync(new ParseRequest(input, RunDirectory, RunDirectory), CancellationToken.None);
}
