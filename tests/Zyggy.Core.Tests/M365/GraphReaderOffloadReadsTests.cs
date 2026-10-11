using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 38 AC-6/AC-7/AC-11: the offload's drive reads — GET only, the drive resolved from the mailbox, never <c>/me</c>; 404 is absent.</summary>
public sealed class GraphReaderOffloadReadsTests : IDisposable
{
    private const string Drive = "b!onedrive0001";
    private readonly GraphFixture _f = new();

    public void Dispose()
    {
        _f.Stub.Violations.Should().BeEmpty();
        _f.Dispose();
    }

    [Fact]
    public async Task OneDriveId_UsersMailboxDrive_SelectIdOnly()
    {
        // Arrange
        _f.Stub.Always("GET", "/drive\\?\\$select=id$", 200, Fixture("drive-id.json"));

        // Act
        var read = await _f.Reader.OneDriveIdAsync(TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().Be(Drive);
        _f.Urls.Should().Contain("GET https://graph.microsoft.com/v1.0/users/alice@acme.example/drive?$select=id");
    }

    [Fact]
    public async Task OneDriveId_403_FailureExitSixForbidden()
    {
        // Arrange
        _f.Stub.Once("GET", "/drive\\?\\$select=id$", 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var read = await _f.Reader.OneDriveIdAsync(TestContext.Current.CancellationToken);

        // Assert
        read.Failure!.ExitCode.Should().Be(6);
        read.Failure.Message.Should().StartWith("forbidden (");
    }

    [Fact]
    public async Task ItemByPath_RootFolder_ParsedFolder()
    {
        // Arrange
        _f.Stub.Always("GET", "/drives/b!onedrive0001/root:/Zyggy/archive\\?", 200, Fixture("root-folder.json"));

        // Act
        var read = await _f.Reader.ItemByPathAsync(Drive, "Zyggy/archive", TestContext.Current.CancellationToken);

        // Assert
        read.Value.Should().Be(new DriveItemInfo(
            "01ROOTARCH", "archive", "01ZYGGYROOT", Drive, 0, null, "https://acme-my.example.invalid/personal/alice/Documents/Zyggy/archive", IsFolder: true));
        _f.Stub.To("root:").Single().Uri.PathAndQuery.Should().Be($"/v1.0/drives/{Drive}/root:/Zyggy/archive?$select={DriveItemInfo.Select}");
    }

    [Fact]
    public async Task ItemByPath_404_Null()
    {
        // Arrange
        _f.Stub.Always("GET", "/root:/", 404, File.ReadAllText(StubGraphHandler.GraphFixture("graph-not-found.json")));

        // Act
        var read = await _f.Reader.ItemByPathAsync(Drive, "Zyggy/archive", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().BeNull();
    }

    [Fact]
    public async Task ItemByPath_File_IsFolderFalse()
    {
        // Arrange
        _f.Stub.Always("GET", "/root:/Zyggy/archive\\?", 200, Fixture("root-is-file.json"));

        // Act
        var read = await _f.Reader.ItemByPathAsync(Drive, "Zyggy/archive", TestContext.Current.CancellationToken);

        // Assert
        read.Value!.IsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task ItemByPath_SegmentWithSpace_Escaped()
    {
        // Arrange
        _f.Stub.Always("GET", "/root:/", 404, "{}");

        // Act
        await _f.Reader.ItemByPathAsync(Drive, "My Files/a#b", TestContext.Current.CancellationToken);

        // Assert
        _f.Stub.To("root:").Single().Uri.AbsoluteUri.Should().Contain("/root:/My%20Files/a%23b?");
    }

    [Fact]
    public async Task ItemById_File_SizeQuickXorParentNameWebUrl()
    {
        // Arrange
        _f.Stub.Always("GET", "/drives/b!onedrive0001/items/01ITEMQUOTE\\?\\$select=", 200, Fixture("item-quote.json"));

        // Act
        var read = await _f.Reader.ItemByIdAsync(Drive, "01ITEMQUOTE", TestContext.Current.CancellationToken);

        // Assert
        read.Value.Should().Be(new DriveItemInfo(
            "01ITEMQUOTE", "quote-2026.txt", "01PROJZYGGY", Drive, 68, "jAt95bF64I8MSmyO3joPMtROLIU=",
            "https://acme-my.example.invalid/personal/alice/Documents/Zyggy/archive/zyggy/quote-2026.txt", IsFolder: false));
    }

    [Fact]
    public async Task ItemById_404_Null()
    {
        // Arrange
        _f.Stub.Always("GET", "/items/01GONE\\?\\$select=", 404, "{}");

        // Act
        var read = await _f.Reader.ItemByIdAsync(Drive, "01GONE", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().BeNull();
    }

    [Fact]
    public async Task ItemById_UnexpectedId_FailureSix()
    {
        // Arrange
        _f.Stub.Always("GET", "/items/01ODD\\?\\$select=", 200, """{"id":"a b/c","name":"x"}""");

        // Act
        var read = await _f.Reader.ItemByIdAsync(Drive, "01ODD", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "Graph returned an unexpected item id"));
    }

    [Fact]
    public async Task Child_Exists_Parsed()
    {
        // Arrange
        _f.Stub.Always("GET", "/items/01ROOTARCH:/zyggy\\?\\$select=", 200, Fixture("project-folder.json"));

        // Act
        var read = await _f.Reader.ChildAsync(Drive, "01ROOTARCH", "zyggy", TestContext.Current.CancellationToken);

        // Assert
        read.Value!.Id.Should().Be("01PROJZYGGY");
        read.Value.ParentId.Should().Be("01ROOTARCH");
        read.Value.IsFolder.Should().BeTrue();
        _f.Stub.To(":/zyggy").Single().Uri.PathAndQuery.Should().Be($"/v1.0/drives/{Drive}/items/01ROOTARCH:/zyggy?$select={DriveItemInfo.Select}");
    }

    [Fact]
    public async Task Child_404_Null()
    {
        // Arrange
        _f.Stub.Always("GET", "/items/01PROJZYGGY:/quote-2026\\.txt\\?\\$select=", 404, "{}");

        // Act
        var read = await _f.Reader.ChildAsync(Drive, "01PROJZYGGY", "quote-2026.txt", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().BeNull();
    }

    [Fact]
    public async Task AllRequests_GetOnly_NeverMe()
    {
        // Arrange
        _f.Stub.Always("GET", "/drive\\?\\$select=id$", 200, Fixture("drive-id.json"));
        _f.Stub.Always("GET", "/root:/", 200, Fixture("root-folder.json"));
        _f.Stub.Always("GET", "/items/", 200, Fixture("item-quote.json"));

        // Act
        await _f.Reader.OneDriveIdAsync(TestContext.Current.CancellationToken);
        await _f.Reader.ItemByPathAsync(Drive, "Zyggy/archive", TestContext.Current.CancellationToken);
        await _f.Reader.ItemByIdAsync(Drive, "01ITEMQUOTE", TestContext.Current.CancellationToken);
        await _f.Reader.ChildAsync(Drive, "01PROJZYGGY", "quote-2026.txt", TestContext.Current.CancellationToken);

        // Assert
        _f.Stub.Requests.Where(r => r.Method != HttpMethod.Post).Should().HaveCount(4).And.OnlyContain(r => r.Method == HttpMethod.Get);
        _f.Stub.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get || r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal));
    }

    private static string Fixture(string name) => File.ReadAllText(StubGraphHandler.GraphFixture(Path.Combine("offload", name)));
}
