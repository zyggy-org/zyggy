using System.Runtime.Versioning;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>The m365 runs' MCP configuration (spec 36 AC-8, plan 36 Assumption 7): only the <c>m365</c> entry of the checkout's <c>.mcp.json</c>, 0600.</summary>
public sealed class RunMcpConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly M365Paths _paths;

    public RunMcpConfigTests()
    {
        Directory.CreateDirectory(Checkout);
        _paths = new M365Paths(new Dictionary<string, string?> { ["HOME"] = _root, ["ZYGGY_STATE_DIR"] = Path.Combine(_root, "state") });
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string Checkout => Path.Combine(_root, "checkout");

    private string McpJson => Path.Combine(Checkout, ".mcp.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Write_TwoServers_OnlyM365VerbatimByteEqualsGolden()
    {
        // Arrange
        File.Copy(Path.Combine(Golden.Directory, "m365", "fixtures", "mcp-with-linkedin.json"), McpJson);

        // Act
        var write = RunMcpConfig.WriteM365Only(Checkout, _paths);

        // Assert
        write.Error.Should().BeNull();
        write.Path.Should().Be(Path.Join(_paths.StateDirectory, "run-mcp.json"));
        File.ReadAllText(write.Path!).Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "m365", "run-mcp.json")));
        File.ReadAllText(write.Path!).Should().NotContain("linkedin");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Write_OnLinux_0600()
    {
        // Arrange
        File.Copy(Path.Combine(Golden.Directory, "m365", "fixtures", "mcp-with-linkedin.json"), McpJson);

        // Act
        var write = RunMcpConfig.WriteM365Only(Checkout, _paths);

        // Assert
        File.GetUnixFileMode(write.Path!).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_paths.StateDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("""{"mcpServers":{"linkedin":{"type":"stdio","command":"zyggy"}}}""")]
    [InlineData("""{"mcpServers":{"m365":"http://x"}}""")]
    [InlineData("""{"servers":{}}""")]
    [InlineData("[]")]
    public void Write_MissingInvalidNoM365_ExitThreeMessage(string? content)
    {
        // Arrange
        if (content is not null)
        {
            File.WriteAllText(McpJson, content);
        }

        // Act
        var write = RunMcpConfig.WriteM365Only(Checkout, _paths);

        // Assert
        write.Path.Should().BeNull();
        write.Error.Should().Be($"configuration error: {McpJson}: no m365 server");
        File.Exists(Path.Join(_paths.StateDirectory, "run-mcp.json")).Should().BeFalse();
    }
}
