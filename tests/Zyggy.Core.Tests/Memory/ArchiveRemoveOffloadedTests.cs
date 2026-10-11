using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-25: <c>archive remove</c> of an offloaded item removes the sidecar as 37 does and notes that the OneDrive copy stays; never Graph.</summary>
public sealed class ArchiveRemoveOffloadedTests : IDisposable
{
    private const string Sidecar = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public ArchiveRemoveOffloadedTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_tree.Root, "home"),
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        _tree.Write("archive/zyggy/quote-2026.md", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", "offload", "sidecar-offloaded.md"))));
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Remove_Offloaded_GitRmSidecarOnlyCommitRemoveNote()
    {
        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        _git.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Equal("rm", "--", Sidecar);
        var commit = _git.CallsOf("commit").Should().ContainSingle().Subject;
        commit.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", Sidecar);
        commit.StandardInput.Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "archive", "commit-message-remove.txt")));
        console.Stderr.Should().Be("archive: note: the OneDrive copy at Zyggy/archive/zyggy/quote-2026.txt stays; delete it in OneDrive if wanted\n");
        console.Stdout.Should().StartWith("removed: archive/zyggy/quote-2026.md\n");
    }

    [Fact]
    public async Task Remove_Offloaded_InboxRemovedLineWithName()
    {
        // Act
        await RunAsync();

        // Assert
        File.ReadAllText(_tree.Full("inbox/remember-2026-09-30.md"))
            .Should().EndWith("- [stated] 2026-09-30 (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"Quote 2026\")\n");
    }

    [Fact]
    public void Remove_Offloaded_NoGraphDependency()
    {
        // Act
        var parameters = typeof(ArchiveService).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);

        // Assert
        parameters.Should().NotContain(t => t.Namespace!.StartsWith("Zyggy.Core.M365", StringComparison.Ordinal));
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync()
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(["remove", "zyggy/quote-2026"], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);
}
