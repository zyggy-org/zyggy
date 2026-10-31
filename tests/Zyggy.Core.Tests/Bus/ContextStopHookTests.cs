using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Memory;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 AC-14..AC-17: one <c>[observed]</c> line per turn into the session's spool, without any key, network or checkout.</summary>
public sealed class ContextStopHookTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 42, 7, TimeSpan.Zero);

    private readonly string _nodeDir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"), "node");

    private readonly FakeTimeProvider _clock = new(T);

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    public static TheoryData<string> BenignSamples() => SecretPatternsTests.BenignSamples();

    private string SpoolFile(string session = HookJson.SessionId) => Path.Combine(ContextSpool.DirectoryFor(_nodeDir, Acme), session + ".md");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_nodeDir)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_FixtureInput_SpoolsObservedLineWithProjectScopeFromCwd()
    {
        // Act
        var outcome = Run(HookJson.Stop(HookJson.FixtureFirstLine + "\nmore", cwd: "/home/alice/src/Calizr"));

        // Assert
        outcome.Should().Be(new StopHookOutcome(StopHookResult.Spooled, null, "0b7c3d1e"));
        File.ReadAllText(SpoolFile()).Should().Be(
            "scope: project:calizr\n- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: Done: the plan for deliverable 27 is written and reviewed.\n");
    }

    [Theory]
    [InlineData("C:\\Users\\alice\\My Project")]
    [InlineData("/")]
    [InlineData("C:\\")]
    [InlineData("/tmp/x_y")]
    public void Run_CwdNotALabel_ScopeGeneralNoHint(string cwd)
    {
        // Act
        Run(HookJson.Stop("note", cwd: cwd));

        // Assert
        File.ReadAllText(SpoolFile()).Should().Be("scope: general\n- [observed] 2026-10-28 [session 0b7c3d1e 10:42Z]: note\n");
    }

    [Fact]
    public void Run_WindowsAndTrailingSeparators_LastSegmentLowerCased()
    {
        // Act
        Run(HookJson.Stop("note", cwd: "C:/src/Calizr/"));

        // Assert
        File.ReadAllText(SpoolFile()).Should().StartWith("scope: project:calizr\n");
    }

    [Theory]
    [InlineData(true, "note")]
    [InlineData(false, "")]
    [InlineData(false, "  \n ")]
    public void Run_StopHookActiveOrEmptyMessage_Ignored_NothingWritten(bool active, string message)
    {
        // Act
        var outcome = Run(HookJson.Stop(message, active: active));

        // Assert
        outcome.Result.Should().Be(StopHookResult.Ignored);
        Directory.Exists(ContextSpool.DirectoryFor(_nodeDir, Acme)).Should().BeFalse();
    }

    [Fact]
    public void Run_InputNotRedirected_Ignored_StdinNeverRead()
    {
        // Arrange
        var stdin = new ThrowingReader();

        // Act
        var outcome = Hook().Run(stdin, inputRedirected: false);

        // Assert
        outcome.Result.Should().Be(StopHookResult.Ignored);
        Directory.Exists(ContextSpool.DirectoryFor(_nodeDir, Acme)).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void Run_Unparseable_Unparseable_NothingWritten(string text)
    {
        // Act
        var outcome = Run(text);

        // Assert
        outcome.Result.Should().Be(StopHookResult.Unparseable);
        Directory.Exists(ContextSpool.DirectoryFor(_nodeDir, Acme)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public void Run_SecretShapedFirstLine_RefusedNamesPattern_NothingSpooled_TextNowhere(string name, string sample)
    {
        // Act
        var outcome = Run(HookJson.Stop("Stored " + sample + " in the vault."));

        // Assert
        outcome.Should().Be(new StopHookOutcome(StopHookResult.Refused, name, "0b7c3d1e"));
        Directory.Exists(ContextSpool.DirectoryFor(_nodeDir, Acme)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public void Run_SecretOnLaterLine_NeverReachesTheFile(string name, string sample)
    {
        // Act
        var outcome = Run(HookJson.Stop("A harmless first line\nthen " + sample));

        // Assert
        outcome.Result.Should().Be(StopHookResult.Spooled, name);
        File.ReadAllText(SpoolFile()).Should().NotContain(sample).And.EndWith(": A harmless first line\n");
    }

    [Theory]
    [MemberData(nameof(BenignSamples))]
    public void Run_BenignSamples_Spooled(string sample)
    {
        // Act + Assert
        Run(HookJson.Stop(sample)).Result.Should().Be(StopHookResult.Spooled, sample);
    }

    [Fact]
    public void Run_SpoolAt150_CapReachedMarkerOnce_ThenAlreadyCapped()
    {
        // Arrange
        for (var i = 0; i < 150; i++)
        {
            Run(HookJson.Stop("turn " + i));
        }

        // Act
        var first = Run(HookJson.Stop("turn 150"));
        var second = Run(HookJson.Stop("turn 151"));

        // Assert
        first.Should().Be(new StopHookOutcome(StopHookResult.CapReached, null, "0b7c3d1e"));
        second.Result.Should().Be(StopHookResult.AlreadyCapped);
        var lines = File.ReadAllLines(SpoolFile());
        lines.Should().HaveCount(152);
        lines.Count(l => l.Contains("cap reached", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void Run_TwoSessionsSameProject_TwoSpools()
    {
        // Act
        Run(HookJson.Stop("one"));
        Run(HookJson.Stop("two", sessionId: "second-session"));

        // Assert
        File.Exists(SpoolFile()).Should().BeTrue();
        File.Exists(SpoolFile("second-session")).Should().BeTrue();
    }

    [Fact]
    public void Run_NoteContainsStatedTag_KeptAsText_LineTagIsObserved()
    {
        // Act
        Run(HookJson.Stop("- [stated] 2026-01-01: not really stated"));

        // Assert
        File.ReadAllText(SpoolFile()).Should().EndWith("- [observed] 2026-10-28 (project:project) [session 0b7c3d1e 10:42Z]: - [stated] 2026-01-01: not really stated\n");
    }

    [Fact]
    public void Run_2330Z_DateAndTimeAreUtc()
    {
        // Arrange
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 28, 23, 30, 0, TimeSpan.Zero));

        // Act
        Run(HookJson.Stop("late"));

        // Assert
        File.ReadAllText(SpoolFile()).Should().Contain("- [observed] 2026-10-28 (project:project) [session 0b7c3d1e 23:30Z]: late");
    }

    private StopHookOutcome Run(string stdin) => Hook().Run(new StringReader(stdin), inputRedirected: true);

    private ContextStopHook Hook()
    {
        var options = new ContextEnvelopeOptions();
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return new ContextStopHook(new ContextSpool(_nodeDir, Acme, _clock, options), patterns, _clock, options);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override int Read() => throw new InvalidOperationException("stdin was read");

        public override string ReadToEnd() => throw new InvalidOperationException("stdin was read");
    }
}
