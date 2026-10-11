using System.Reflection;
using System.Text;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 38 AC-5/AC-6/AC-10: exactly two create-only writes, the scope checked before any request, every outcome classified.</summary>
public sealed class GraphDriveWriterTests : IDisposable
{
    private const string Drive = "b!onedrive0001";
    private const string Root = "01ROOTARCH";
    private const string Project = "01PROJZYGGY";
    private const string FolderUrl = "/drives/b!onedrive0001/items/01ROOTARCH/children$";
    private const string FileUrl = "/drives/b!onedrive0001/items/01PROJZYGGY:/quote-2026\\.txt:/content";

    private static readonly byte[] Item = Encoding.UTF8.GetBytes("Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n");

    private readonly GraphFixture _f = new();
    private readonly GraphDriveWriter _writer;

    public GraphDriveWriterTests()
    {
        _f.Stub.AllowDriveWrites();
        var scope = new DriveWriteScope(Drive, Root);
        scope.KnowProjectFolder(Project);
        _writer = new GraphDriveWriter(_f.Tokens, _f.Http, scope);
    }

    public void Dispose()
    {
        _f.Stub.Violations.Should().BeEmpty();
        _f.Dispose();
    }

    [Fact]
    public async Task CreateFolder_PostChildrenWithFailConflict_ReturnsItem()
    {
        // Arrange
        _f.Stub.Always("POST", FolderUrl, 201, Fixture("created-folder.json"));

        // Act
        var write = await _writer.CreateFolderAsync(Drive, Root, Slug.Parse("zyggy"), TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.None);
        write.Value!.Id.Should().Be(Project);
        var request = _f.Stub.To("/children").Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/drives/b!onedrive0001/items/01ROOTARCH/children");
        request.Body.Should().Be("""{"name":"zyggy","folder":{},"@microsoft.graph.conflictBehavior":"fail"}""");
        request.Headers["Content-Type"].Should().StartWith("application/json");
        request.Headers["Authorization"].Should().StartWith("Bearer ");
    }

    [Fact]
    public async Task CreateFile_PutNewFileFormWithFailConflictOctetStream_BytesExact()
    {
        // Arrange
        _f.Stub.Always("PUT", FileUrl, 201, Fixture("created-file.json"));

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Value!.QuickXorHash.Should().Be("jAt95bF64I8MSmyO3joPMtROLIU=");
        var request = _f.Stub.To(":/content").Single();
        request.Method.Should().Be(HttpMethod.Put);
        request.Uri.AbsoluteUri.Should().Be(
            "https://graph.microsoft.com/v1.0/drives/b!onedrive0001/items/01PROJZYGGY:/quote-2026.txt:/content?@microsoft.graph.conflictBehavior=fail");
        request.Bytes.Should().Equal(Item);
        request.Headers["Content-Type"].Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task CreateFile_ScopeRefuses_NoRequestSent()
    {
        // Act
        var write = await _writer.CreateFileAsync(Drive, Root, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.Refused);
        write.Detail.Should().Be("file_parent_not_project_folder");
        write.Sent.Should().BeFalse();
        _f.Stub.Requests.Should().BeEmpty("a refused write sends nothing, not even the token request");
    }

    [Fact]
    public async Task CreateFolder_ScopeRefusesGrandchild_NoRequestSent()
    {
        // Act
        var write = await _writer.CreateFolderAsync(Drive, Project, Slug.Parse("nested"), TestContext.Current.CancellationToken);

        // Assert
        write.Detail.Should().Be("folder_parent_not_root");
        _f.Stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFolder_Created_FileInsideAllowed()
    {
        // Arrange: a folder just created becomes a project folder for the scope
        var scope = new DriveWriteScope(Drive, Root);
        var writer = new GraphDriveWriter(_f.Tokens, _f.Http, scope);
        _f.Stub.Always("POST", FolderUrl, 201, Fixture("created-folder.json"));
        _f.Stub.Always("PUT", FileUrl, 201, Fixture("created-file.json"));

        // Act
        await writer.CreateFolderAsync(Drive, Root, Slug.Parse("zyggy"), TestContext.Current.CancellationToken);
        var file = await writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        file.Failure.Should().Be(GraphWriteFailure.None);
    }

    [Theory]
    [InlineData(409, "error-409.json", nameof(GraphWriteFailure.Conflict), "nameAlreadyExists")]
    [InlineData(403, "../graph-forbidden.json", nameof(GraphWriteFailure.Forbidden), null)]
    [InlineData(400, "error-blocked-type.json", nameof(GraphWriteFailure.TypeBlocked), "fileTypeBlocked")]
    [InlineData(400, null, nameof(GraphWriteFailure.ClientError), "400")]
    [InlineData(500, null, nameof(GraphWriteFailure.ServerError), "500")]
    public async Task CreateFile_Status_Classified(int status, string? body, string expected, string? detail)
    {
        // Arrange
        _f.Stub.Always("PUT", FileUrl, status, body is null ? "{}" : Fixture(body));

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(Enum.Parse<GraphWriteFailure>(expected));
        write.Status.Should().Be(status);
        write.Value.Should().BeNull();
        if (detail is not null)
        {
            write.Detail.Should().Be(detail);
        }
    }

    [Fact]
    public async Task CreateFile_BlockedTypeByMessage_TypeBlocked()
    {
        // Arrange: A9 — an unknown code whose message names the extension
        _f.Stub.Always("PUT", FileUrl, 400, """{"error":{"code":"invalidRequest","message":"This extension is not allowed."}}""");

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.TypeBlocked);
    }

    [Fact]
    public async Task CreateFile_429SixTimes_Throttled()
    {
        // Arrange
        _f.Stub.Always("PUT", FileUrl, 429, "{}");

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.Throttled);
        _f.Stub.To(":/content").Should().HaveCount(GraphHttp.Retries + 1);
        _f.Waits.Should().HaveCount(GraphHttp.Retries);
        _f.Stub.To(":/content").Should().OnlyContain(r => r.Bytes!.SequenceEqual(Item), "the content is rebuilt for every attempt");
    }

    [Fact]
    public async Task CreateFile_Transport_Transport()
    {
        // Arrange
        _f.Stub.OnceThrow("PUT", FileUrl, "Connection reset https://graph.microsoft.com/x?secret=1");

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.Transport);
        write.Detail.Should().NotContain("?").And.NotContain("secret");
    }

    [Fact]
    public async Task CreateFile_401Then201_ReMintsOnce()
    {
        // Arrange
        _f.Stub.Once("PUT", FileUrl, 401, "{}");
        _f.Stub.Always("PUT", FileUrl, 201, Fixture("created-file.json"));

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.None);
        _f.TokenPosts.Should().Be(2);
    }

    [Fact]
    public async Task CreateFile_401Twice_Auth()
    {
        // Arrange
        _f.Stub.Always("PUT", FileUrl, 401, "{}");

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.Auth);
        _f.TokenPosts.Should().Be(2);
    }

    [Fact]
    public async Task CreateFile_MintFails_AuthNoWrite()
    {
        // Arrange
        _f.Stub.Once("POST", "/oauth2/v2\\.0/token$", 401, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        var write = await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        write.Failure.Should().Be(GraphWriteFailure.Auth);
        _f.Stub.To(":/content").Should().BeEmpty();
    }

    [Fact]
    public void Writer_Surface_ExactlyTwoWriteOperations()
    {
        // Act
        var operations = typeof(IGraphDriveWriter).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);
        var forbidden = typeof(GraphDriveWriter).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, "Delete|Move|Rename|Copy|Share|Permission|Invite|Session|Replace|Update|Mail"));

        // Assert
        operations.Should().Equal("CreateFileAsync", "CreateFolderAsync");
        forbidden.Should().BeEmpty();
    }

    [Fact]
    public async Task Writer_NeverMeNeverOtherHost()
    {
        // Arrange
        _f.Stub.Always("POST", FolderUrl, 201, Fixture("created-folder.json"));
        _f.Stub.Always("PUT", FileUrl, 201, Fixture("created-file.json"));

        // Act
        await _writer.CreateFolderAsync(Drive, Root, Slug.Parse("zyggy"), TestContext.Current.CancellationToken);
        await _writer.CreateFileAsync(Drive, Project, "quote-2026.txt", Item, TestContext.Current.CancellationToken);

        // Assert
        _f.Stub.Requests.Should().OnlyContain(r =>
            r.Uri.AbsolutePath.StartsWith("/v1.0/drives/", StringComparison.Ordinal) || r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal));
    }

    private static string Fixture(string name) => File.ReadAllText(StubGraphHandler.GraphFixture(Path.Combine("offload", name)));
}
