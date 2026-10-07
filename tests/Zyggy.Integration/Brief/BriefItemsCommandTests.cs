using Microsoft.Extensions.Time.Testing;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Brief;

/// <summary>
/// Spec 35 AC-24 (I): <c>zyggy brief items</c> as the session calls it — in process with the stubbed Graph for the statuses (the binary has
/// no test switch), and from the built binary for usage and configuration.
/// </summary>
public sealed class BriefItemsCommandTests : IDisposable
{
    private static readonly FakeTimeProvider Tuesday = new(new DateTimeOffset(2026, 10, 6, 8, 45, 0, TimeSpan.Zero));

    private readonly M365RunHarness _run = new();

    public BriefItemsCommandTests()
    {
        var briefDirectory = Path.Combine(_run.Fixture.StateDirectory, "brief");
        Directory.CreateDirectory(briefDirectory);
        File.Copy(M365InstanceFixture.Golden("brief", "items-sidecar.json"), Path.Combine(briefDirectory, "brief-2026-10-06.json"));
    }

    public void Dispose() => _run.Dispose();

    [Fact]
    public async Task Items_InProcess_StatusesFromStub()
    {
        // Act
        var (exit, console) = await BriefInProcess.RunAsync(_run.Env(), _run.Graph, ["items", "Z1,Z4-Z6,Z9"], Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[(l.LastIndexOf("\"status\":", StringComparison.Ordinal) + 9)..])
            .Should().Equal("\"ok\"}", "\"moved\"}", "\"deleted\"}", "\"deleted\"}", "\"unknown\"}");
    }

    [Fact]
    public async Task Items_InProcess_FileOther_ExpandsToOneLinePerMail()
    {
        // Act
        var (exit, console) = await BriefInProcess.RunAsync(_run.Env(), _run.Graph, ["items", "Z7"], Tuesday);

        // Assert: three mails, three lines, one already filed by hand
        exit.Should().Be(0, console.Stderr);
        var lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3).And.OnlyContain(l => l.StartsWith("{\"n\":7,\"kind\":\"move\",\"group\":\"file-other\",", StringComparison.Ordinal));
        lines.Select(l => l.EndsWith("\"status\":\"moved\"}", StringComparison.Ordinal)).Should().Equal(false, false, true);
    }

    [Fact]
    public async Task Items_InProcess_GraphFailure_ExitSixNoLine()
    {
        // Arrange
        _run.Graph.Once("GET", "/messages/m13\\?", 403, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "m365", "graph", "graph-forbidden.json")));

        // Act
        var (exit, console) = await BriefInProcess.RunAsync(_run.Env(), _run.Graph, ["items", "Z1,Z2"], Tuesday);

        // Assert: the session acts on nothing
        exit.Should().Be(6);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().StartWith("brief: ");
    }

    [Theory]
    [InlineData("do Z1")]
    [InlineData("Z0")]
    [InlineData("Z3-Z1")]
    public async Task Items_Binary_BadSelector_ExitFour(string selector)
    {
        // Act
        var run = await ZyggyCli.RunAsync(["brief", "items", selector], _run.Env(), null, _run.Fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().EndWith("(usage: zyggy brief items <Zn[,Zm…]|Zn-Zm|all> [--date <YYYY-MM-DD>])\n");
    }

    [Fact]
    public async Task Items_Binary_NoSidecar_ExitThree()
    {
        // Act
        var run = await ZyggyCli.RunAsync(["brief", "items", "Z1", "--date", "2026-10-01"], _run.Env(), null, _run.Fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("brief: no brief for 2026-10-01\n");
    }
}
