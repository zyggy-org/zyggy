using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// <c>graph.sh</c>'s retry, re-mint and refusal rules (spec 33 AC-15; bats "429 ×2 then 200 … 403 ErrorAccessDenied").
/// </summary>
public sealed class GraphRetryTests : IDisposable
{
    private const string Folders = "mailFolders\\?";

    private readonly GraphFixture _graph = new();

    private static string RetryAfter2 => File.ReadAllText(StubGraphHandler.GraphFixture("retry-after-2.hdr"));

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
    }

    private int FolderRequests => _graph.Urls.Count(u => u.Contains("mailFolders?", StringComparison.Ordinal));

    [Fact]
    public async Task TwoTimes429Then200_Ok_WaitsRetryAfter()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 429, "{}", RetryAfter2).Once("GET", Folders, 429, "{}", RetryAfter2);

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().BeNull();
        FolderRequests.Should().Be(3);
        _graph.TokenPosts.Should().Be(1);
        _graph.Waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Without429RetryAfter_WaitsPowerOfTwo()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 503).Once("GET", Folders, 429).Once("GET", Folders, 503);

        // Act
        await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        _graph.Waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Once503Then200_Ok()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 503);

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().BeNull();
        FolderRequests.Should().Be(2);
    }

    [Fact]
    public async Task Six429_ExitSixThrottled()
    {
        // Arrange
        for (var i = 0; i < 7; i++)
        {
            _graph.Stub.Once("GET", Folders, 429, "{}", RetryAfter2);
        }

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "throttled (429) after 5 retries — runbook 13 \"Throttling\""));
        FolderRequests.Should().Be(6);
    }

    [Fact]
    public async Task Once401_OneRemintThenOk()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 401);

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().BeNull();
        _graph.TokenPosts.Should().Be(2);
        FolderRequests.Should().Be(2);
    }

    [Fact]
    public async Task Twice401_ExitSix()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 401).Once("GET", Folders, 401);

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "unauthorized (401) after a fresh token — runbook 13 \"Certificate rejected\""));
        _graph.TokenPosts.Should().Be(2);
    }

    [Fact]
    public async Task Forbidden403_ExitSixForbiddenCode()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "forbidden (ErrorAccessDenied) — runbook 13 \"Scope or grant missing\""));
        FolderRequests.Should().Be(1);
    }

    [Fact]
    public async Task Redirect302_NotFollowed_ExitSix()
    {
        // Arrange
        _graph.Stub.Once("GET", Folders, 302, "{}", "Location: https://evil.example/x\n");

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "Graph request failed (302)"));
        _graph.Urls.Should().NotContain(u => u.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransportErrorWithSecretText_Withheld()
    {
        // Arrange
        _graph.Stub.OnceThrow("GET", Folders, "proxy answered password: hunter2secret");

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "request failed (curl error text withheld: matches secret pattern credential-assignment)"));
    }

    [Fact]
    public async Task MintFailure_ReturnedAsFailure()
    {
        // Arrange
        _graph.Stub.Once("POST", "/oauth2/v2\\.0/token$", 400, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "auth failed (invalid_client) — runbook 13 \"Certificate rejected\""));
        FolderRequests.Should().Be(0);
    }
}
