using System.Text.Json;

using Zyggy.Core.Jobs;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Tests.Jobs.Hook;

/// <summary>
/// <c>zyggy hook pre-tool-use</c> (spec 15 AC-1, AC-5): allow is exit 0 with no output and no blocked line; deny is exit 2,
/// one stderr line and one appended blocked line; every failure of its own is a deny with <c>hook error</c>.
/// </summary>
public sealed class JobHookVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-hook-tests", Guid.NewGuid().ToString("N"));

    public JobHookVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "shop"));
    }

    private string Root => Path.Combine(_dir, "shop");

    private string BlockedFile => Path.Combine(_dir, "run.blocked");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp folder is harmless.
        }
    }

    private static string Call(string tool, object input) =>
        JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = tool, tool_input = input });

    private string[] Args(params string[] rules) =>
        ["pre-tool-use", "--root", Root, "--blocked-file", BlockedFile, .. rules.SelectMany(r => new[] { "--allow", r })];

    private static Task<int> RunAsync(VerbIo io, string[] args) => new JobHookVerb(ignoreCase: false).RunAsync(args, io, Ct);

    [Fact]
    public async Task Run_AllowedCall_ExitZeroNoOutputNoBlockedLine()
    {
        // Arrange
        var console = new VerbConsole(Call("Read", new { file_path = Path.Combine(Root, "a.cs") }));

        // Act
        var exit = await RunAsync(console.Io, Args("Read"));

        // Assert
        (exit, console.Stdout, console.Stderr, File.Exists(BlockedFile)).Should().Be((0, "", "", false));
    }

    [Fact]
    public async Task Run_CallOutsideAllowedTools_ExitTwoWithOneStderrLine()
    {
        // Arrange
        var console = new VerbConsole(Call("Bash", new { command = "rm -rf src" }));

        // Act
        var exit = await RunAsync(console.Io, Args("Read"));

        // Assert
        (exit, console.Stdout, console.Stderr).Should().Be((2, "", "zyggy: blocked Bash: not in allowed_tools (rm -rf src)\n"));
    }

    [Fact]
    public async Task Run_DeniedCall_AppendsOneBlockedLine()
    {
        // Arrange
        await File.WriteAllTextAsync(BlockedFile, "Read: not in allowed_tools (x)\n", Ct);
        var console = new VerbConsole(Call("PowerShell", new { command = "iwr https://example.invalid" }));

        // Act
        await RunAsync(console.Io, Args("PowerShell"));

        // Assert
        (await File.ReadAllTextAsync(BlockedFile, Ct)).Should()
            .Be("Read: not in allowed_tools (x)\nPowerShell: network program (iwr https://example.invalid)\n");
    }

    [Fact]
    public async Task Run_WriteOutsideProject_DeniedWithWriteRule()
    {
        // Arrange
        var console = new VerbConsole(Call("Write", new { file_path = Path.Combine(_dir, "x.txt"), content = "x" }));

        // Act
        var exit = await RunAsync(console.Io, Args("Write"));

        // Assert
        (exit, console.Stderr).Should().Be((2, $"zyggy: blocked Write: write outside the project ({Path.Combine(_dir, "x.txt")})\n"));
    }

    [Fact]
    public async Task Run_NoAllowRules_DeniesEverything()
    {
        // Arrange
        var console = new VerbConsole(Call("Read", new { file_path = Path.Combine(Root, "a.cs") }));

        // Act
        var exit = await RunAsync(console.Io, Args());

        // Assert
        exit.Should().Be(2);
    }

    [Fact]
    public async Task Run_UsesCwdFromTheInput()
    {
        // Arrange
        var input = JsonSerializer.Serialize(new { tool_name = "Write", tool_input = new { file_path = "x.cs" }, cwd = _dir });
        var console = new VerbConsole(input);

        // Act
        var exit = await RunAsync(console.Io, Args("Write"));

        // Assert
        exit.Should().Be(2);
    }

    // AC-5: every failure of its own denies with "hook error".
    public static TheoryData<string> BadInputs() => new()
    {
        "",
        "not json",
        "[]",
        """{"tool_input":{}}""",
        """{"tool_name":"Read"}""",
        """{"tool_name":"Read","tool_input":"x"}""",
    };

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task Run_BadInput_DeniedAsHookError(string stdin)
    {
        // Arrange
        var console = new VerbConsole(stdin);

        // Act
        var exit = await RunAsync(console.Io, Args("Read"));

        // Assert
        (exit, console.Stderr).Should().Be((2, "zyggy: blocked unknown: hook error (hook input is not a PreToolUse call)\n"));
    }

    // Space-separated argument lists; "r" stands for an absolute root, "relative" for a relative one.
    public static TheoryData<string> BadArguments() => new()
    {
        "pre-tool-use",
        "pre-tool-use --blocked-file b",
        "pre-tool-use --root r",
        "pre-tool-use --root r --blocked-file b --allow",
        "pre-tool-use --root r --blocked-file b --bogus x",
        "pre-tool-use --root relative --blocked-file b",
        "post-tool-use --root r --blocked-file b",
        "",
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task Run_BadArguments_DeniedAsHookError(string argumentLine)
    {
        // Arrange
        var console = new VerbConsole(Call("Read", new { file_path = "a.cs" }));
        var args = argumentLine.Replace(" r ", $" {Root} ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Act
        var exit = await RunAsync(console.Io, args);

        // Assert
        $"{exit} {console.Stderr}".Should().StartWith("2 zyggy: blocked Read: hook error (");
    }

    [Fact]
    public async Task Run_ReaderThrows_DeniedAsHookError()
    {
        // Arrange
        var console = new VerbConsole();
        var io = console.Io with { In = new ThrowingReader() };

        // Act
        var exit = await RunAsync(io, Args("Read"));

        // Assert
        (exit, console.Stderr).Should().Be((2, "zyggy: blocked unknown: hook error (internal error)\n"));
    }

    [Fact]
    public async Task Run_BlockedFileUnwritable_StillDenied()
    {
        // Arrange
        var console = new VerbConsole(Call("Bash", new { command = "rm x" }));
        var args = new[] { "pre-tool-use", "--root", Root, "--blocked-file", Path.Combine(_dir, "missing-folder", "run.blocked"), "--allow", "Read" };

        // Act
        var exit = await RunAsync(console.Io, args);

        // Assert
        exit.Should().Be(2);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => throw new IOException("stdin broke");

        public override string ReadToEnd() => throw new IOException("stdin broke");
    }
}
