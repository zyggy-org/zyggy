using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 Step 4: the per-session spool under <c>&lt;nodeDir&gt;/context/&lt;tenant&gt;/</c> — node state, never a bus path.</summary>
public sealed class ContextSpoolTests : IDisposable
{
    private const string Session = "0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly ContextScope Calizr = ContextScope.Parse("project:calizr");

    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 42, 7, TimeSpan.Zero);

    private readonly string _nodeDir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"), "node");

    private readonly ContextSpool _spool;

    public ContextSpoolTests() => _spool = new ContextSpool(_nodeDir, Acme, new FakeTimeProvider(T), new ContextEnvelopeOptions());

    private string File(string session) => Path.Combine(ContextSpool.DirectoryFor(_nodeDir, Acme), session + ".md");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_nodeDir)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryFor_NodeDirAndTenant_ContextTenantUnderNodeDir()
    {
        // Act + Assert
        ContextSpool.DirectoryFor(_nodeDir, Acme).Should().Be(Path.Join(_nodeDir, "context", "acme"));
        _spool.Directory.Should().Be(Path.Join(_nodeDir, "context", "acme"));
    }

    [Fact]
    public void Append_FirstLine_CreatesDirectoryAndFileWithScopeHeader_Appended()
    {
        // Act
        var result = _spool.Append(Session, Calizr, "- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: one");

        // Assert
        result.Should().Be(SpoolAppendResult.Appended);
        System.IO.File.ReadAllBytes(File(Session)).Should().Equal(Encoding.UTF8.GetBytes("scope: project:calizr\n- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: one\n"));
    }

    [Fact]
    public void Append_SecondLine_AppendedAfterTheFirst_HeaderOnce()
    {
        // Arrange
        _spool.Append(Session, Calizr, "- one");

        // Act
        _spool.Append(Session, Calizr, "- two");

        // Assert
        System.IO.File.ReadAllText(File(Session)).Should().Be("scope: project:calizr\n- one\n- two\n");
    }

    [Fact]
    public void Append_150LinesPresent_MarkerOnceCapReached_ThenAlreadyCapped()
    {
        // Arrange
        for (var i = 0; i < 150; i++)
        {
            _spool.Append(Session, Calizr, "- line " + i);
        }

        // Act
        var first = _spool.Append(Session, Calizr, "- line 150");
        var second = _spool.Append(Session, Calizr, "- line 151");

        // Assert
        first.Should().Be(SpoolAppendResult.CapReached);
        second.Should().Be(SpoolAppendResult.AlreadyCapped);
        var lines = System.IO.File.ReadAllText(File(Session)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(152);
        lines[^1].Should().Be("- [observed] cap reached: no further hook lines this session");
        lines.Should().NotContain("- line 150").And.NotContain("- line 151");
    }

    [Fact]
    public void Append_149Lines_OneMoreAppendedNoMarker()
    {
        // Arrange
        for (var i = 0; i < 149; i++)
        {
            _spool.Append(Session, Calizr, "- line " + i);
        }

        // Act
        var result = _spool.Append(Session, Calizr, "- line 149");

        // Assert
        result.Should().Be(SpoolAppendResult.Appended);
        System.IO.File.ReadAllText(File(Session)).Should().NotContain("cap reached").And.EndWith("- line 149\n");
    }

    [Fact]
    public void Due_NamedSessionAnyAge_Included()
    {
        // Arrange
        _spool.Append(Session, Calizr, "- one");

        // Act
        var due = _spool.Due(Session, T);

        // Assert
        due.Should().ContainSingle().Which.SessionId.Should().Be(Session);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(61, true)]
    public void Due_UnnamedFreshExcluded_OlderThan60MinutesIncluded(int minutesOld, bool included)
    {
        // Arrange
        _spool.Append("other-session", Calizr, "- one");
        System.IO.File.SetLastWriteTimeUtc(File("other-session"), T.UtcDateTime.AddMinutes(-minutesOld));

        // Act
        var due = _spool.Due(Session, T);

        // Assert
        due.Select(f => f.SessionId).Should().Equal(included ? ["other-session"] : []);
    }

    [Fact]
    public void Due_OrderNamedFirstThenStaleByName()
    {
        // Arrange
        foreach (var session in new[] { "zzz", "aaa", Session })
        {
            _spool.Append(session, Calizr, "- one");
            System.IO.File.SetLastWriteTimeUtc(File(session), T.UtcDateTime.AddHours(-2));
        }

        // Act
        var due = _spool.Due(Session, T);

        // Assert
        due.Select(f => f.SessionId).Should().Equal(Session, "aaa", "zzz");
    }

    [Fact]
    public void Due_DirectoryAbsent_Empty()
    {
        // Act + Assert
        _spool.Due(Session, T).Should().BeEmpty();
    }

    [Fact]
    public void Due_SessionIdNull_StaleOnly()
    {
        // Arrange
        _spool.Append("fresh", Calizr, "- one");
        _spool.Append("stale", Calizr, "- one");
        System.IO.File.SetLastWriteTimeUtc(File("stale"), T.UtcDateTime.AddHours(-2));

        // Act
        var due = _spool.Due(null, T);

        // Assert
        due.Select(f => f.SessionId).Should().Equal("stale");
    }

    [Fact]
    public void Due_ParsesScopeHeaderAndBodyLines_InOrder()
    {
        // Arrange
        _spool.Append(Session, Calizr, "- one");
        _spool.Append(Session, Calizr, "- two");

        // Act
        var file = _spool.Due(Session, T).Single();

        // Assert
        file.Scope.Should().Be(Calizr);
        file.Lines.Should().Equal("- one", "- two");
        file.Path.Should().Be(File(Session));
    }

    [Fact]
    public void Due_MalformedFirstLine_GeneralAndEveryLineIsBody()
    {
        // Arrange
        Directory.CreateDirectory(_spool.Directory);
        System.IO.File.WriteAllText(File(Session), "- one\n- two\n");

        // Act
        var file = _spool.Due(Session, T).Single();

        // Assert
        file.Scope.Should().Be(ContextScope.Parse("general"));
        file.Lines.Should().Equal("- one", "- two");
    }

    [Fact]
    public void Delete_RemovesTheFile_OtherSpoolsUntouched()
    {
        // Arrange
        _spool.Append(Session, Calizr, "- one");
        _spool.Append("other", Calizr, "- one");
        var file = _spool.Due(Session, T).Single();

        // Act
        _spool.Delete(file);

        // Assert
        System.IO.File.Exists(File(Session)).Should().BeFalse();
        System.IO.File.Exists(File("other")).Should().BeTrue();
    }

    [Fact]
    public void Delete_NodeDirWithForwardSlashes_DeletesTheFile()
    {
        // Arrange: node.json carries forward slashes on every OS; the enumerated file uses the platform separator
        var spool = new ContextSpool(_nodeDir.Replace('\\', '/'), Acme, new FakeTimeProvider(T), new ContextEnvelopeOptions());
        spool.Append(Session, Calizr, "- one");
        var file = spool.Due(Session, T).Single();

        // Act
        spool.Delete(file);

        // Assert
        System.IO.File.Exists(File(Session)).Should().BeFalse();
    }

    [Fact]
    public void Append_NoTempFileSurvives()
    {
        // Act
        _spool.Append(Session, Calizr, "- one");
        _spool.Append(Session, Calizr, "- two");

        // Assert
        Directory.EnumerateFiles(_spool.Directory).Select(Path.GetFileName).Should().Equal(Session + ".md");
    }
}
