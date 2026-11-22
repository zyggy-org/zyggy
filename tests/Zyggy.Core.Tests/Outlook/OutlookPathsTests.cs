using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Verify 01M81XMXM722ESMJ8M0Y6VBP6N (spec 58 slice 7, AC-12): the outlook guard compares the full path a call names with the state
/// folder, so <c>..</c>, <c>.</c>, <c>\\?\</c>, an admin share (UNC), an 8.3 short name, a junction or a symbolic link cannot reach the
/// pass file or the approval key under another spelling.
/// </summary>
public sealed class OutlookPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", "longdirectoryname" + Guid.NewGuid().ToString("N")[..8]);

    public OutlookPathsTests() => Directory.CreateDirectory(State);

    private string State => Path.Combine(_root, "node", "work", "acme", "outlook");

    private static readonly Dictionary<string, string?> Environment = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("../outlook/pending.jsonl")]
    [InlineData("./../outlook/./approval-2026-11-22.key")]
    [InlineData("../../acme/outlook")]
    public void Resolve_DotSegments_UnderTheStateFolder(string relative)
    {
        // Arrange
        string cwd = Path.Combine(_root, "node", "work", "acme", "brief");

        // Act
        string resolved = OutlookPaths.Resolve(relative, cwd, Environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, cwd, Environment)).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"\\?\{state}\pending.jsonl")]
    [InlineData(@"\\.\{state}\pending.jsonl")]
    [InlineData(@"//?/{state}/pending.jsonl")]
    [InlineData(@"\\localhost\{drive}$\{rest}\pending.jsonl")]
    [InlineData(@"\\127.0.0.1\{drive}$\{rest}\approval.key")]
    [InlineData(@"\\?\UNC\localhost\{drive}$\{rest}\pending.jsonl")]
    public void Resolve_DeviceAndUncForms_UnderTheStateFolder(string form)
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path forms");
        string path = form.Replace("{state}", State, StringComparison.Ordinal)
            .Replace("{drive}", State[..1], StringComparison.Ordinal)
            .Replace("{rest}", State[3..], StringComparison.Ordinal);

        // Act
        string resolved = OutlookPaths.Resolve(path, _root, Environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, _root, Environment)).Should().BeTrue();
    }

    [Fact]
    public void Resolve_ShortName_UnderTheStateFolder()
    {
        // Arrange: the 8.3 name of the test's long folder name, where the volume keeps 8.3 names.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 names are Windows only");
        string shortRoot = Path.Combine(Path.GetDirectoryName(_root)!, "LONGDI~1");
        Assert.SkipUnless(Directory.Exists(shortRoot) && Directory.GetDirectories(Path.GetDirectoryName(_root)!, "longdirectoryname*").Length == 1, "no unique 8.3 name on this volume");
        string path = Path.Combine(shortRoot, "node", "work", "acme", "outlook", "pending.jsonl");

        // Act
        string resolved = OutlookPaths.Resolve(path, _root, Environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, _root, Environment)).Should().BeTrue();
    }

    [Fact]
    public void Resolve_SymbolicLinkToTheStateFolder_UnderTheStateFolder()
    {
        // Arrange
        string link = Path.Combine(_root, "elsewhere");
        try
        {
            Directory.CreateSymbolicLink(link, State);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("this account may not create symbolic links");
        }

        // Act
        string resolved = OutlookPaths.Resolve(Path.Combine(link, "pending.jsonl"), _root, Environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, _root, Environment)).Should().BeTrue();
    }

    [Fact]
    public void Resolve_EnvironmentVariable_Expanded()
    {
        // Arrange
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_UT_ROOT"] = _root };

        // Act
        string resolved = OutlookPaths.Resolve("$ZYGGY_UT_ROOT/node/work/acme/outlook/pending.jsonl", Path.GetTempPath(), environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, _root, Environment)).Should().BeTrue();
    }

    [Theory]
    [InlineData("../brief/notes.md")]
    [InlineData("../outlook-old/pending.jsonl")]
    public void Resolve_Elsewhere_NotUnderTheStateFolder(string relative)
    {
        // Arrange
        string cwd = Path.Combine(_root, "node", "work", "acme", "brief");

        // Act
        string resolved = OutlookPaths.Resolve(relative, cwd, Environment);

        // Assert
        OutlookPaths.IsUnder(resolved, OutlookPaths.Resolve(State, cwd, Environment)).Should().BeFalse();
    }
}
