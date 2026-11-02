using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// Plan 41 A14: the pickup's Graph exchange never sleeps — built with <c>retries: 0</c> a throttled answer returns at once and carries
/// <c>Retry-After</c>, which the fetcher turns into its hold; the m365 verbs and the offload keep their five retries.
/// </summary>
public sealed class GraphHttpRetryAfterTests
{
    private const string Folders = "mailFolders\\?";

    private const string FoldersUrl = "https://graph.microsoft.com/v1.0/users/alice@acme.example/mailFolders?$top=100";

    private static string RetryAfter2 => File.ReadAllText(StubGraphHandler.GraphFixture("retry-after-2.hdr"));

    [Fact]
    public async Task SendAsync_RetriesZero_429_ReturnsAtOnceWithRetryAfter_NoDelay()
    {
        // Arrange
        using var graph = new GraphFixture(retries: 0);
        graph.Stub.Once("GET", Folders, 429, "{}", RetryAfter2);

        // Act
        var result = await graph.Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, FoldersUrl), _ => false, TestContext.Current.CancellationToken);

        // Assert
        result.Response.Should().BeNull();
        result.Failure.Should().StartWith("throttled (429)");
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        result.Status.Should().Be(429);
        graph.Waits.Should().BeEmpty();
        graph.Stub.Requests.Should().ContainSingle();
        graph.Stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_DefaultRetries_Unchanged()
    {
        // Arrange
        using var graph = new GraphFixture();
        graph.Stub.Once("GET", Folders, 429, "{}", RetryAfter2).Once("GET", Folders, 429, "{}", RetryAfter2);

        // Act
        var result = await graph.Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, FoldersUrl), _ => false, TestContext.Current.CancellationToken);

        // Assert
        result.Failure.Should().BeNull();
        result.Response!.Status.Should().Be(200);
        graph.Waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        graph.Stub.Requests.Should().HaveCount(3);
        graph.Stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_RetriesZero_429_FailureCarriesRetryAfterAndStatus()
    {
        // Arrange
        using var graph = new GraphFixture(retries: 0);
        graph.Stub.Once("GET", Folders, 429, "{}", RetryAfter2);

        // Act
        var read = await graph.Reader.MailFoldersAsync(TestContext.Current.CancellationToken);

        // Assert
        read.Failure!.Message.Should().StartWith("throttled (429)");
        read.Failure.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        read.Failure.Status.Should().Be(429);
        GraphFailureClass.Classify(read.Failure).Should().Be(GraphFailureKind.Throttled);
        graph.Waits.Should().BeEmpty();
        graph.Stub.Violations.Should().BeEmpty();
    }
}
