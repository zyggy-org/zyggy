using System.Text;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// Plan 41 A15 (spec 41 AC-29): Central's pickup downloads one OneDrive file read-only — GET <c>root:/&lt;path&gt;:/content</c> with the
/// bearer token, Graph's 302 followed once to the tenant's <c>-my</c> download host without the token, the body capped at the signed size.
/// </summary>
public sealed class GraphReaderDownloadTests : IDisposable
{
    private const string Drive = "b!onedrive0001";

    private const string FilePath = "Zyggy/archive/calizr/staging-restore-notes.txt";

    private const string Content = "/drives/b!onedrive0001/root:/Zyggy/archive/calizr/staging-restore-notes.txt:/content$";

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Restore\n\nSteps for the staging restore.\n");

    private readonly GraphFixture _f = new();

    public void Dispose()
    {
        _f.Stub.Violations.Should().BeEmpty();
        _f.Dispose();
    }

    [Theory]
    [InlineData("https://acme-my.sharepoint.com/download/01ABC", true)]
    [InlineData("https://contoso-dev-my.sharepoint.com/personal/x", true)]
    [InlineData("http://acme-my.sharepoint.com/download/01ABC", false)]
    [InlineData("https://acme.sharepoint.com/download/01ABC", false)]
    [InlineData("https://acme-my.sharepoint.com.example.invalid/download", false)]
    [InlineData("https://graph.microsoft.com/v1.0/drives", false)]
    public void IsDownloadHost_OnlyHttpsTenantMyHost(string uri, bool expected)
    {
        // Act
        var allowed = GraphEndpoints.IsDownloadHost(new Uri(uri));

        // Assert
        allowed.Should().Be(expected);
    }

    [Fact]
    public async Task DownloadAsync_ContentRedirectToMyHost_FollowedOnceWithoutAuthorization_BytesReturned()
    {
        // Arrange
        var drive = new StubOneDrive();
        var file = drive.Add(drive.Add(StubOneDrive.RootId, "calizr"), "staging-restore-notes.txt", Notes);
        _f.Stub.ReadDrive(drive);

        // Act
        var read = await _f.Reader.DownloadAsync(Drive, FilePath, Notes.Length, TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().Equal(Notes);
        _f.Stub.To(":/content").Should().ContainSingle().Which.Headers["Authorization"].Should().StartWith("Bearer ");
        var download = _f.Stub.Requests.Should().ContainSingle(r => r.Uri.Host == StubGraphHandler.Download).Subject;
        download.Uri.AbsolutePath.Should().Be("/download/" + file);
        download.Headers.Should().NotContainKey("Authorization");
        drive.Downloads.Should().Be(1);
    }

    [Fact]
    public async Task DownloadAsync_RedirectToOtherHost_Failure_NoSecondRequest()
    {
        // Arrange
        _f.Stub.Once("GET", Content, 302, string.Empty, "Location: https://files.example.invalid/download/01ABC");

        // Act
        var read = await _f.Reader.DownloadAsync(Drive, FilePath, Notes.Length, TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "download redirect to an unexpected host"));
        _f.Stub.Requests.Should().NotContain(r => r.Uri.Host == "files.example.invalid");
    }

    [Fact]
    public async Task DownloadAsync_LargerThanMaxBytes_Failure()
    {
        // Arrange
        var drive = new StubOneDrive();
        drive.Add(drive.Add(StubOneDrive.RootId, "calizr"), "staging-restore-notes.txt", Notes);
        _f.Stub.ReadDrive(drive);

        // Act
        var read = await _f.Reader.DownloadAsync(Drive, FilePath, Notes.Length - 1, TestContext.Current.CancellationToken);

        // Assert
        read.Value.Should().BeNull();
        read.Failure!.ExitCode.Should().Be(6);
        read.Failure.Message.Should().Be($"download larger than {Notes.Length - 1} bytes");
    }

    [Fact]
    public async Task DownloadAsync_404_OkNull()
    {
        // Arrange
        _f.Stub.ReadDrive(new StubOneDrive());

        // Act
        var read = await _f.Reader.DownloadAsync(Drive, FilePath, Notes.Length, TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsync_NoViolationsInStub()
    {
        // Arrange
        var drive = new StubOneDrive();
        drive.Add(drive.Add(StubOneDrive.RootId, "calizr"), "staging-restore-notes.txt", Notes);
        _f.Stub.ReadDrive(drive);

        // Act
        await _f.Reader.DownloadAsync(Drive, FilePath, Notes.Length, TestContext.Current.CancellationToken);

        // Assert
        _f.Stub.Violations.Should().BeEmpty();
        _f.Stub.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get || (r.Method == HttpMethod.Post && r.Uri.Host == "login.microsoftonline.com"));
        _f.Stub.Requests.Select(r => r.Uri.Host).Distinct().Should().BeSubsetOf(["login.microsoftonline.com", "graph.microsoft.com", StubGraphHandler.Download]);
    }
}
