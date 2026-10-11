using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 38 AC-8..AC-15, AC-18, AC-29, AC-30: one offload run — folder, upload or adopt, verification by id, and only then the sidecar,
/// one <c>archive offload</c> commit removing the item, the inbox line; one action-log row per write; no model, no secret anywhere.
/// </summary>
public sealed class OffloadServiceTests : IDisposable
{
    private const string ItemRepo = "acme/alice/archive/zyggy/quote-2026.txt";
    private const string SidecarRepo = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly OffloadFixture _f = new();

    public void Dispose()
    {
        _f.Graph.Stub.Violations.Should().BeEmpty();
        _f.Dispose();
    }

    [Fact]
    public async Task Run_OneIndexedItem_MovedFolderCreatedFileUploadedVerifiedCommittedPushed()
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.NextIds.Enqueue("01PROJZYGGY");
        _f.Drive.NextIds.Enqueue("01ITEMQUOTE");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        run.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Key = "zyggy/quote-2026",
            ItemPath = "archive/zyggy/quote-2026.txt",
            DrivePath = "Zyggy/archive/zyggy/quote-2026.txt",
            Outcome = OffloadOutcome.Moved,
            Pushed = true,
        });
        _f.Drive.Posts.Should().Be(1);
        _f.Drive.Puts.Should().Be(1);
        _f.Graph.Stub.To("/items/01ITEMQUOTE\\?").Should().ContainSingle("one verification read by id");
        _f.Git.CallsOf("rm").Single().Arguments.Should().Equal("rm", "--", ItemRepo);
        _f.Git.CallsOf("add").Single().Arguments.Should().Equal("add", "--", SidecarRepo);
        var commit = _f.Git.CallsOf("commit").Single();
        commit.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", ItemRepo, SidecarRepo);
        commit.StandardInput.Should().Be(OffloadFixture.Golden("offload/commit-message-offload.txt"));
        _f.Git.CallsOf("push").Single().Arguments.Should().Equal("push", "origin", "HEAD:main");
    }

    [Fact]
    public async Task Run_Moved_SidecarEqualsOffloadedGolden()
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.NextIds.Enqueue("01PROJZYGGY");
        _f.Drive.NextIds.Enqueue("01ITEMQUOTE");

        // Act
        await _f.RunAsync();

        // Assert
        File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md")).Should().Be(OffloadFixture.Golden("offload/sidecar-offloaded.md"));
    }

    [Fact]
    public async Task Run_Moved_InboxLineEqualsGolden()
    {
        // Arrange
        _f.QuoteItem();

        // Act
        await _f.RunAsync();

        // Assert: the line's bytes (plan assumption A3); MemoryLine's reading of it is not asserted here
        File.ReadAllText(_f.Tree.Full("inbox/remember-2026-10-01.md")).Should().Be(OffloadFixture.Golden("offload/inbox-after-offload.md"));
    }

    [Fact]
    public async Task Run_Order_NoGitCallBeforeVerification()
    {
        // Arrange
        _f.QuoteItem();
        var sidecarBefore = File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md"));
        var verified = false;
        var sidecarAtFirstGitWrite = (string?)null;
        _f.Git.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) is "rm" or "add" or "commit" && sidecarAtFirstGitWrite is null)
            {
                verified = _f.Graph.Stub.Requests.Any(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith("/items/01ID0002", StringComparison.Ordinal));
                sidecarAtFirstGitWrite = File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md"));
            }

            return null;
        };

        // Act
        await _f.RunAsync();

        // Assert
        verified.Should().BeTrue("the copy is re-read by id before the first git write");
        sidecarAtFirstGitWrite.Should().NotBe(sidecarBefore).And.Contain("drive_item_id: 01ID0002");
    }

    [Fact]
    public async Task Run_Order_VerifyFails_SidecarUnchangedNoGitWrite()
    {
        // Arrange
        _f.QuoteItem();
        var sidecarBefore = File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md"));
        _f.Drive.CorruptNextHash();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Reason.Should().Be(OffloadReason.VerifyFailed);
        File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md")).Should().Be(sidecarBefore);
        _f.Git.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["rm", "add", "commit"]);
    }

    [Fact]
    public async Task Run_TwoItemsSameProject_OneFolderTwoUploadsTwoCommits()
    {
        // Arrange
        _f.QuoteItem();
        _f.Item("roof-photo", ImageBytes.Png(2, 2), extension: "png");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        run.Items.Select(i => i.ItemPath).Should().Equal("archive/zyggy/quote-2026.txt", "archive/zyggy/roof-photo.png");
        _f.Drive.Posts.Should().Be(1);
        _f.Drive.Puts.Should().Be(2);
        _f.Git.CallsOf("commit").Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_ProjectFolderExists_Reused()
    {
        // Arrange: the owner created the project folder
        _f.QuoteItem();
        _f.Drive.Add(StubOneDrive.RootId, "zyggy", id: "01PROJZYGGY");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        _f.Drive.Posts.Should().Be(0);
        _f.Drive.File("01PROJZYGGY", "quote-2026.txt").Should().Equal(Encoding.UTF8.GetBytes(OffloadFixture.TextItem));
    }

    [Fact]
    public async Task Run_FolderCreate409_ReReadAndUsed()
    {
        // Arrange: the folder appears between the read and the create (the create answers 409)
        _f.QuoteItem();
        _f.Graph.Stub.Once("GET", "/items/01ROOTARCH:/zyggy\\?", 404, "{}");
        _f.Drive.Add(StubOneDrive.RootId, "zyggy", id: "01PROJZYGGY");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        _f.Drive.Posts.Should().Be(1);
        _f.Drive.Puts.Should().Be(1);
        _f.ActionRows[0].Should().Contain("\"tool\":\"archive-offload-folder\"").And.Contain("\"status\":\"error: nameAlreadyExists\"");
    }

    [Fact]
    public async Task Run_TargetIdenticalExists_AdoptedNoUploadCommitted()
    {
        // Arrange: an earlier run uploaded the file, then stopped before its commit
        _f.QuoteItem();
        var folder = _f.Drive.Add(StubOneDrive.RootId, "zyggy");
        _f.Drive.Add(folder, "quote-2026.txt", Encoding.UTF8.GetBytes(OffloadFixture.TextItem));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Outcome.Should().Be(OffloadOutcome.Adopted);
        _f.Drive.Puts.Should().Be(0);
        _f.Git.CallsOf("commit").Should().ContainSingle();
    }

    [Fact]
    public async Task Run_EachWrite_OneActionRowOkOrError()
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.NextIds.Enqueue("01PROJZYGGY");

        // Act
        await _f.RunAsync();

        // Assert
        _f.ActionRows.Should().Equal(
            """{"ts":"2026-10-01T03:30:00Z","session_id":"archive-offload 2026-10-01T03:30:00Z","tool":"archive-offload-folder","summary":"drive b!onedrive0001 parent 01ROOTARCH name zyggy size 0","status":"ok"}""",
            """{"ts":"2026-10-01T03:30:00Z","session_id":"archive-offload 2026-10-01T03:30:00Z","tool":"archive-offload-upload","summary":"drive b!onedrive0001 parent 01PROJZYGGY name quote-2026.txt size 68","status":"ok"}""");
    }

    [Fact]
    public async Task Run_WebUrlMatchesContactPattern_StoredWithoutUrlNote()
    {
        // Arrange: a webUrl that carries an e-mail address (as some tenants' personal-site URLs do)
        _f.QuoteItem();
        _f.Drive.NextIds.Enqueue("01PROJZYGGY");
        _f.Drive.NextIds.Enqueue("01ITEMQUOTE");
        _f.Graph.Stub.Once("GET", "/items/01ITEMQUOTE\\?", 200, File.ReadAllText(StubGraphHandler.GraphFixture("offload/item-quote.json"))
            .Replace("personal/alice/", "personal/alice@acme.example/", StringComparison.Ordinal));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Note.Should().Contain("contact detail");
        File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md")).Should().Be(OffloadFixture.Golden("offload/sidecar-offloaded-no-url.md"));
    }

    [Fact]
    public async Task Run_UploadedBytes_AreExactlyTheRepositoryBytes()
    {
        // Arrange
        _f.QuoteItem();
        var bytes = File.ReadAllBytes(_f.Tree.Full("archive/zyggy/quote-2026.txt"));

        // Act
        await _f.RunAsync();

        // Assert
        var folder = _f.Drive.ChildId(StubOneDrive.RootId, "zyggy")!;
        _f.Drive.File(folder, "quote-2026.txt").Should().Equal(bytes);
    }

    [Fact]
    public async Task Run_ReadsBackOnlyMetadata()
    {
        // Arrange
        _f.QuoteItem();

        // Act
        await _f.RunAsync();

        // Assert
        _f.Graph.Stub.Requests.Where(r => r.Method == HttpMethod.Get).Should().NotContain(r => r.Uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_NoModelRunnerAnywhere()
    {
        // Act
        var types = typeof(OffloadService).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(typeof(OffloadService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Select(f => f.FieldType));

        // Assert
        types.Should().NotContain(t => t.Namespace == "Zyggy.Core.Models" || t.Name.Contains("ModelRunner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_Caps_TwentyFirstItemWaits()
    {
        // Arrange: 21 indexed items with the same bytes
        for (var i = 1; i <= 21; i++)
        {
            _f.Item($"item-{i:D2}", Encoding.UTF8.GetBytes(OffloadFixture.TextItem));
        }

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        run.Items.Should().HaveCount(20);
        File.Exists(_f.Tree.Full("archive/zyggy/item-21.txt")).Should().BeTrue();
        File.ReadAllText(_f.Tree.Full("archive/zyggy/item-21.md")).Should().NotContain("offloaded:");
    }

    [Fact]
    public async Task Run_SecretsNeverInGitArgsOrRows()
    {
        // Arrange
        _f.QuoteItem();

        // Act
        await _f.RunAsync();

        // Assert
        var token = File.ReadAllText(StubGraphHandler.GraphFixture("token-ok.json"));
        var accessToken = System.Text.Json.JsonDocument.Parse(token).RootElement.GetProperty("access_token").GetString()!;
        var gitText = string.Join("\n", _f.Git.Calls.SelectMany(c => c.Arguments.Append(c.StandardInput ?? string.Empty)));
        var rows = string.Join("\n", _f.ActionRows);
        foreach (var text in new[] { gitText, rows })
        {
            text.Should().NotContain(accessToken).And.NotContain("PRIVATE KEY").And.NotContain("Bearer");
        }

        _f.Git.Calls.Should().OnlyContain(c => c.Environment == null || !c.Environment.Values.Any(v => v != null && v.Contains(accessToken, StringComparison.Ordinal)));
    }
}
