using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The Graph reads of <c>graph.sh</c> (spec 33 AC-16) over the shell's fixture responses: mail folders, drives, drive files, drafts,
/// message sender, item exists and item kind. Never <c>/me</c>, never a write, never plain HTTP (the stub's violations).
/// </summary>
public sealed class GraphReaderTests : IDisposable
{
    private readonly GraphFixture _graph = new();

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
    }

    [Fact]
    public async Task MailFolders_TopLevelPlusOneChildLevel_ExcludedFromConfig()
    {
        // Act
        var read = await _graph.Reader.MailFoldersAsync(CancellationToken.None);

        // Assert
        read.Failure.Should().BeNull();
        var folders = read.Value!;
        folders.Should().HaveCount(6);
        folders.Where(f => f.Excluded).Select(f => f.WellKnownName).Should().Equal("deleteditems", "drafts", "junkemail");
        folders[0].Should().Be(new MailFolder("AQMkInbox0001", "Inbox", "inbox", 1240, false));
        folders[5].Should().Be(new MailFolder("AQMkArchive0001", "Archive", null, 90, false));
        _graph.Urls.Should().Contain("GET https://graph.microsoft.com/v1.0/users/alice@acme.example/mailFolders?$top=100");
        _graph.TokenPosts.Should().Be(1);
    }

    [Fact]
    public async Task Drives_OneDrivePlusGrantedSites_FirstIdKeptExclusionsByIdOrName()
    {
        // Act
        var all = await _graph.Reader.DrivesAsync(CancellationToken.None);
        using var excluded = new GraphFixture("""{"drives":{"exclude_drives":["b!opsarchive0001","ops"]}}""");
        var narrowed = await excluded.Reader.DrivesAsync(CancellationToken.None);

        // Assert
        const string ops = "acme.sharepoint.example,aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa,bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        all.Value.Should().Equal(
            new GraphDrive("b!onedrive0001", "OneDrive", "onedrive"),
            new GraphDrive("b!ops0001", "ops", ops),
            new GraphDrive("b!opsarchive0001", "ops-archive", ops));
        narrowed.Value.Should().Equal(new GraphDrive("b!onedrive0001", "OneDrive", "onedrive"));
        excluded.Stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task DriveFiles_AllPagesPathsRebuiltLastWinsSorted()
    {
        // Act
        var read = await _graph.Reader.DriveFilesAsync("b!onedrive0001", CancellationToken.None);

        // Assert
        read.Failure.Should().BeNull();
        var files = read.Value!;
        files.Select(f => f.Id).Should().Equal(
            "01F01", "01F02", "01F03", "01F04", "01F05", "01F06", "01F07", "01F08", "01F09", "01F10",
            "01F11", "01F12", "01F13", "01F14", "01F15", "01F16", "01F17", "01F19", "01F20");
        files[0].Should().Be(new DriveFile("01F01", "/Reports/report-01.docx", 20480, "2026-09-01T08:00:00Z"));
        files.Should().Contain(new DriveFile("01F14", "/Reports/Q3/plan.pdf", 102400, "2026-09-02T08:00:00Z"));
        files.Should().Contain(new DriveFile("01F17", "/notes.md", 512, "2026-09-02T09:30:00Z"));
        files.Should().NotContain(f => f.Path.Contains("gone.docx", StringComparison.Ordinal));
        _graph.Urls.Count(u => u.Contains("/delta?", StringComparison.Ordinal)).Should().Be(2);
        _graph.Urls.Should().Contain("GET https://graph.microsoft.com/v1.0/drives/b!onedrive0001/items/01ROOT0001/delta?token=page2");
        DriveFile.ToJsonLine(files[0]).Should().Be("""{"id":"01F01","path":"/Reports/report-01.docx","size":20480,"modified":"2026-09-01T08:00:00Z"}""");
    }

    [Fact]
    public async Task DriveFiles_ForeignNextLink_ExitSix()
    {
        // Arrange
        _graph.Stub.Once("GET", "drives/b!onedrive0001.root/delta", 200, File.ReadAllText(Infrastructure.StubGraphHandler.GraphFixture("drive-delta-foreign-next.json")));

        // Act
        var read = await _graph.Reader.DriveFilesAsync("b!onedrive0001", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "Graph returned a nextLink outside https://graph.microsoft.com/v1.0"));
        _graph.Urls.Should().NotContain(u => u.Contains("evil.example", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(403, "drive b!ops0001: 403 (not granted)")]
    [InlineData(404, "drive b!ops0001: 404 (not found)")]
    public async Task DriveFiles_403Or404_RefusedFiveNamed(int status, string message)
    {
        // Arrange
        _graph.Stub.Once("GET", "drives/b!ops0001.root/delta", status, "{}");

        // Act
        var read = await _graph.Reader.DriveFilesAsync("b!ops0001", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(5, message));
    }

    [Fact]
    public async Task DriveFiles_Over2000Pages_ExitSix()
    {
        // Arrange: every page points to the next
        _graph.Stub.Always("GET", "drives/b!loop0001", 200,
            """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/b!loop0001/items/x/delta?token=next"}""");

        // Act
        var read = await _graph.Reader.DriveFilesAsync("b!loop0001", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "drive b!loop0001: more than 2000 delta pages"));
        _graph.Urls.Count(u => u.Contains("b!loop0001", StringComparison.Ordinal)).Should().Be(2000);
    }

    [Fact]
    public async Task DraftsSince_FilterSelectTopAndPreferHeader()
    {
        // Act
        var read = await _graph.Reader.DraftsSinceAsync("2026-09-30T00:00:00Z", CancellationToken.None);

        // Assert
        read.Value!.Select(d => d.GetProperty("id").GetString()).Should().Equal("d0", "d1");
        read.Value![1].GetProperty("subject").GetString().Should().Be("RE: Invoice 2026-41");
        var request = _graph.Stub.Requests.Single(r => r.Method == HttpMethod.Get);
        Uri.UnescapeDataString(request.Uri.ToString()).Should().Be(
            "https://graph.microsoft.com/v1.0/users/alice@acme.example/mailFolders/drafts/messages?$filter=createdDateTime ge 2026-09-30T00:00:00Z" +
            "&$select=id,subject,toRecipients,ccRecipients,bccRecipients,conversationId,createdDateTime,changeKey,body&$top=50");
        request.Headers["Prefer"].Should().Be("outlook.body-content-type=\"text\"");
    }

    [Fact]
    public async Task MessageSender_LowerCasedFromReplyToConversation()
    {
        // Act
        var m1 = await _graph.Reader.MessageSenderAsync("m1", CancellationToken.None);
        var m2 = await _graph.Reader.MessageSenderAsync("m2", CancellationToken.None);

        // Assert
        MessageSender.ToJson(m1.Value!).Should().Be("""{"from":"carol@example.org","replyTo":[],"conversationId":"c1"}""");
        MessageSender.ToJson(m2.Value!).Should().Be("""{"from":"dave@example.org","replyTo":["erin@example.org"],"conversationId":"c2"}""");
        _graph.Urls.Should().Contain("GET https://graph.microsoft.com/v1.0/users/alice@acme.example/messages/m1?$select=from,replyTo,conversationId");
    }

    [Fact]
    public async Task MessageSender_404_NotFoundExitSix()
    {
        // Arrange
        _graph.Stub.Once("GET", "messages/m1\\?", 404, File.ReadAllText(Infrastructure.StubGraphHandler.GraphFixture("graph-not-found.json")));

        // Act
        var read = await _graph.Reader.MessageSenderAsync("m1", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "not found (m1)"));
    }

    [Fact]
    public async Task ItemExists_200Exists404Absent_NameUrlEncoded()
    {
        // Act
        var existing = await _graph.Reader.ItemExistsAsync("b!onedrive0001", "01PARENT0001", "Plan.md", CancellationToken.None);
        var absent = await _graph.Reader.ItemExistsAsync("b!onedrive0001", "01PARENT0001", "New notes.md", CancellationToken.None);

        // Assert
        existing.Value.Should().Be(ItemPresence.Exists);
        absent.Value.Should().Be(ItemPresence.Absent);
        _graph.Stub.Requests.Select(r => r.Uri.AbsoluteUri).Should().Contain(
            "https://graph.microsoft.com/v1.0/drives/b!onedrive0001/items/01PARENT0001:/New%20notes.md?$select=id");
    }

    [Fact]
    public async Task ItemExists_500_GraphRequestFailed()
    {
        // Arrange
        _graph.Stub.Once("GET", "items/01PARENT0001./Plan\\.md", 500, "{}");

        // Act
        var read = await _graph.Reader.ItemExistsAsync("b!onedrive0001", "01PARENT0001", "Plan.md", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "Graph request failed (500)"));
    }

    [Theory]
    [InlineData("01PARENT0001", "Folder")]
    [InlineData("01ROOT0001", "Folder")]
    [InlineData("01FILE0001", "File")]
    [InlineData("01GONE0001", "Absent")]
    public async Task ItemKind_FolderRootFileAbsent(string item, string kind)
    {
        // Act
        var read = await _graph.Reader.ItemKindAsync("b!onedrive0001", item, CancellationToken.None);

        // Assert
        read.Value.ToString().Should().Be(kind);
    }

    [Fact]
    public async Task ItemKind_403_Forbidden()
    {
        // Arrange
        _graph.Stub.Once("GET", "items/01FILE0001\\?", 403, File.ReadAllText(Infrastructure.StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var read = await _graph.Reader.ItemKindAsync("b!onedrive0001", "01FILE0001", CancellationToken.None);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "forbidden (ErrorAccessDenied) — runbook 13 \"Scope or grant missing\""));
    }

    [Fact]
    public async Task AnyRead_OnlyTokenPostAndGets()
    {
        // Act
        await _graph.Reader.MailFoldersAsync(CancellationToken.None);
        await _graph.Reader.DrivesAsync(CancellationToken.None);
        await _graph.Reader.DriveFilesAsync("b!onedrive0001", CancellationToken.None);
        await _graph.Reader.ItemKindAsync("b!onedrive0001", "01FILE0001", CancellationToken.None);

        // Assert
        _graph.Stub.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get || r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal));
        _graph.Stub.Requests.Should().OnlyContain(r => r.Uri.Scheme == "https");
        _graph.Stub.Requests.Where(r => r.Method == HttpMethod.Get).Should().OnlyContain(r => r.Headers["Authorization"].StartsWith("Bearer ", StringComparison.Ordinal));
        _graph.TokenPosts.Should().Be(1);
    }
}
