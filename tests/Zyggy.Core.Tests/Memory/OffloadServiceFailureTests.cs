using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 38 AC-7, AC-15..AC-20, AC-22: a failing offload never loses bytes — run-level failures stop before any item is half-done (exit 6),
/// transient ones are counted and parked at the limit, permanent ones park at once, an interrupted commit is resumed, a deferred push
/// stops the run (exit 7), and the exit code follows 6 &gt; 7 &gt; 5 &gt; 0.
/// </summary>
public sealed class OffloadServiceFailureTests : IDisposable
{
    private const string Key = "zyggy/quote-2026";
    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";
    private const string FileUrl = "/drives/b!onedrive0001/items/01PROJZYGGY:/quote-2026\\.txt:/content";

    private OffloadFixture _f = new();

    public void Dispose()
    {
        _f.Graph.Stub.Violations.Should().BeEmpty();
        _f.Dispose();
    }

    // --- Run level -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_RootMissing_ExitSixNoWriteNoGitWrite()
    {
        // Arrange
        Reset(withRoot: false);
        _f.QuoteItem();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunReason.Should().Be(OffloadReason.RootMissing);
        run.RunMessage.Should().Be("root folder Zyggy/archive missing or not a folder — runbook \"Offload root folder\"");
        _f.Drive.Posts.Should().Be(0, "the binary never creates the root or anything above it");
        _f.Drive.Puts.Should().Be(0);
        NoGitWrite();
    }

    [Fact]
    public async Task Run_RootIsFile_ExitSixRootNotFolder()
    {
        // Arrange
        _f.Drive.RootIsFile();
        _f.QuoteItem();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunReason.Should().Be(OffloadReason.RootNotFolder);
        _f.Drive.Posts.Should().Be(0);
    }

    [Fact]
    public async Task Run_NeverCreatesRootOrAbove()
    {
        // Arrange
        _f.QuoteItem();
        _f.Item("other", Encoding.UTF8.GetBytes(OffloadFixture.TextItem), project: "garden");

        // Act
        await _f.RunAsync();

        // Assert: every folder creation has the root folder as parent
        _f.Graph.Stub.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/children", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(r => r.Uri.AbsolutePath == "/v1.0/drives/b!onedrive0001/items/01ROOTARCH/children");
    }

    [Fact]
    public async Task Run_MintFails_ExitSixNoItemTouched()
    {
        // Arrange
        _f.QuoteItem();
        _f.Graph.Stub.Once("POST", "/oauth2/v2\\.0/token$", 401, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunReason.Should().Be(OffloadReason.AuthFailed);
        run.Items.Should().BeEmpty();
        NoGitWrite();
    }

    [Fact]
    public async Task Run_PreflightNotOnBranch_ExitSixBeforeAnyGraphWrite()
    {
        // Arrange
        _f.QuoteItem();
        _f.Git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunMessage.Should().Be("git error: not_on_branch");
        _f.Graph.Stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PreflightPushPending_ExitSixBeforeUpload()
    {
        // Arrange: an earlier archive commit is still unpushed and cannot be pushed now
        _f.QuoteItem();
        _f.Git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\narchive add zyggy/other.txt\n"));
        _f.Git.On("push", RecordingProcessRunner.Fail(128, "fatal: unable to access the remote"));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunMessage.Should().Be("git error: push_pending");
        _f.Drive.Puts.Should().Be(0);
    }

    [Fact]
    public async Task Run_Forbidden403OnFolder_ExitSixRunStopsNotParked()
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.RefuseNextWith(403, "accessDenied");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunReason.Should().Be(OffloadReason.Forbidden);
        run.RunMessage.Should().Contain("runbook \"Grant OneDrive write for the archive folder\"");
        _f.State.Items.Should().BeEmpty("a missing grant is not the item's fault");
        _f.ActionRows.Should().ContainSingle().Which.Should().Contain("\"status\":\"error: accessDenied\"");
        NoGitWrite();
    }

    [Fact]
    public async Task Run_ActionLogUnwritable_StopsBeforeNextWrite()
    {
        // Arrange: the action log's directory is a file, so the row cannot be appended
        _f.QuoteItem();
        Directory.CreateDirectory(Path.GetDirectoryName(_f.ActionLogDirectory)!);
        File.WriteAllText(_f.ActionLogDirectory, "not a directory");

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(6);
        run.RunReason.Should().Be(OffloadReason.LogUnwritable);
        _f.Drive.Posts.Should().Be(1);
        _f.Drive.Puts.Should().Be(0, "no write follows a row that could not be appended");
        NoGitWrite();
    }

    // --- Per item ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("transport_error")]
    [InlineData("server_error")]
    [InlineData("throttled")]
    [InlineData("auth_failed")]
    public async Task Run_Transient_ItemUntouchedCountedRetriedNextRun(string reason)
    {
        // Arrange
        _f.QuoteItem();
        var item = File.ReadAllBytes(_f.Tree.Full("archive/zyggy/quote-2026.txt"));
        var sidecar = File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md"));
        _f.Drive.Add(StubOneDrive.RootId, "zyggy", id: "01PROJZYGGY");

        // Act
        var counts = new List<(int Failures, bool Parked)>();
        for (var run = 0; run < 3; run++)
        {
            Inject(reason);
            var result = await _f.RunAsync();
            result.Items.Single().Reason.Should().Be(OffloadReasonWire.TryFromWire(reason, out var r) ? r : null);
            counts.Add((_f.State.Items[Key].Failures, _f.State.Items[Key].Parked));
        }

        // Assert
        counts.Should().Equal((1, false), (2, false), (3, true));
        File.ReadAllBytes(_f.Tree.Full("archive/zyggy/quote-2026.txt")).Should().Equal(item);
        File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md")).Should().Be(sidecar);
        NoGitWrite();
    }

    [Theory]
    [InlineData("sha_mismatch")]
    [InlineData("write_refused")]
    [InlineData("target_conflict")]
    [InlineData("verify_failed")]
    [InlineData("type_blocked")]
    [InlineData("too_large")]
    public async Task Run_Permanent_ParkedAtOnceNeverRetried(string reason)
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.Add(StubOneDrive.RootId, "zyggy", id: "01PROJZYGGY");
        switch (reason)
        {
            case "sha_mismatch":
                _f.Tree.Write("archive/zyggy/quote-2026.txt", "changed after archiving\n");
                break;
            case "write_refused":
                _f.Drive.RefuseNextWith(400, "invalidRequest");
                break;
            case "target_conflict":
                _f.Drive.Add("01PROJZYGGY", "quote-2026.txt", Encoding.UTF8.GetBytes("a different file\n"));
                break;
            case "verify_failed":
                _f.Drive.CorruptNextHash();
                break;
            case "type_blocked":
                _f.Drive.RefuseNextWith(400, "fileTypeBlocked");
                break;
            default:
                _f.SimpleUploadLimit = 10;
                break;
        }

        // Act
        var first = await _f.RunAsync();
        var second = await _f.RunAsync();

        // Assert
        first.ExitCode.Should().Be(5);
        first.Items.Single().Should().BeEquivalentTo(new { Outcome = OffloadOutcome.Parked, Reason = OffloadReasonWire.TryFromWire(reason, out var r) ? r : (OffloadReason?)null });
        _f.State.Items[Key].Should().BeEquivalentTo(new { Failures = 1, Parked = true });
        second.Items.Should().BeEmpty("a parked item is never retried automatically");
        second.ExitCode.Should().Be(5);
        File.Exists(_f.Tree.Full("archive/zyggy/quote-2026.txt")).Should().BeTrue();
        NoGitWrite();
    }

    [Fact]
    public async Task Run_VerifyFailed_CopyLeftOnDriveNothingInGit()
    {
        // Arrange
        _f.QuoteItem();
        _f.Drive.CorruptNextHash();

        // Act
        await _f.RunAsync();

        // Assert
        _f.Drive.File(_f.Drive.ChildId(StubOneDrive.RootId, "zyggy")!, "quote-2026.txt").Should().NotBeNull();
        _f.Graph.Stub.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
        NoGitWrite();
    }

    [Fact]
    public async Task Run_OneItemFails_OtherCandidatesStillMoved()
    {
        // Arrange: the first item's copy fails its verification; the second moves
        _f.QuoteItem();
        _f.Item("roof-quote", Encoding.UTF8.GetBytes(OffloadFixture.TextItem));
        _f.Drive.CorruptNextHash();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Select(i => (i.Key, i.Outcome)).Should().Equal((Key, OffloadOutcome.Parked), ("zyggy/roof-quote", OffloadOutcome.Moved));
        run.ExitCode.Should().Be(5);
        _f.Git.CallsOf("commit").Should().ContainSingle();
    }

    // --- Git after the sidecar -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_CommitFailsAfterSidecar_ItemRestoredFromHeadSidecarKeepsOffloadGitError()
    {
        // Arrange
        _f.QuoteItem();
        _f.Git.On("commit", RecordingProcessRunner.Fail(1, "fatal: cannot commit"));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Should().BeEquivalentTo(new { Outcome = OffloadOutcome.Failed, Reason = OffloadReason.GitError });
        _f.Git.CallsOf("checkout").Single().Arguments.Should().Equal("checkout", "HEAD", "--", "acme/alice/archive/zyggy/quote-2026.txt");
        File.ReadAllText(_f.Tree.Full("archive/zyggy/quote-2026.md")).Should().Contain("offloaded: 2026-10-01");
        _f.State.Items[Key].Should().BeEquivalentTo(new { Failures = 1, Reason = OffloadReason.GitError, Parked = false });
        run.ExitCode.Should().Be(5);
    }

    [Fact]
    public async Task Run_NextRun_ResumeReVerifiesAndCommits()
    {
        // Arrange: the first run's commit failed after the upload
        _f.QuoteItem();
        _f.Git.On("commit", RecordingProcessRunner.Fail(1, "fatal: cannot commit"));
        await _f.RunAsync();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.RunMessage);
        run.Items.Single().Outcome.Should().Be(OffloadOutcome.Moved);
        _f.Drive.Puts.Should().Be(1, "the resume re-verifies the copy the sidecar names and uploads nothing");
        _f.Git.CallsOf("commit").Should().HaveCount(2);
        _f.State.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Resume_VerifyFails_SidecarRestoredFromHeadParked()
    {
        // Arrange
        _f.QuoteItem();
        _f.Git.On("commit", RecordingProcessRunner.Fail(1, "fatal: cannot commit"));
        await _f.RunAsync();
        _f.Drive.CorruptNextHash();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Should().BeEquivalentTo(new { Outcome = OffloadOutcome.Parked, Reason = OffloadReason.VerifyFailed });
        _f.Git.CallsOf("checkout").Last().Arguments.Should().Equal("checkout", "HEAD", "--", "acme/alice/archive/zyggy/quote-2026.md");
    }

    [Fact]
    public async Task Run_PushDeferred_ExitSevenStopsAfterItem()
    {
        // Arrange
        _f.QuoteItem();
        _f.Item("roof-quote", Encoding.UTF8.GetBytes(OffloadFixture.TextItem));
        _f.Git.On("push", RecordingProcessRunner.Fail(1, Rejected));
        _f.Git.On("rebase", RecordingProcessRunner.Fail(1, "CONFLICT"));

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(7);
        run.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Key, Outcome = OffloadOutcome.Moved, Pushed = false, Reason = OffloadReason.PushDeferred });
        File.ReadAllText(_f.Tree.Full("archive/zyggy/roof-quote.md")).Should().NotContain("offloaded:");
    }

    [Fact]
    public async Task Run_ExitPrecedence_SixOverSevenOverFiveOverZero()
    {
        // Arrange: a parked item (5) and a push deferred (7) → 7
        _f.State.Park("zyggy/old", OffloadReason.TargetConflict, new DateOnly(2026, 9, 30));
        _f.QuoteItem();
        _f.Git.On("push", RecordingProcessRunner.Fail(1, Rejected));
        _f.Git.On("rebase", RecordingProcessRunner.Fail(1, "CONFLICT"));
        var seven = await _f.RunAsync();

        // A parked item and a run-level stop → 6
        Reset();
        _f.State.Park("zyggy/old", OffloadReason.TargetConflict, new DateOnly(2026, 9, 30));
        _f.QuoteItem();
        _f.Drive.RootIsFile();
        var six = await _f.RunAsync();

        // Assert
        seven.ExitCode.Should().Be(7);
        six.ExitCode.Should().Be(6);
    }

    [Fact]
    public async Task Run_PreviouslyParkedItem_ExitFive()
    {
        // Arrange
        _f.State.Park("zyggy/old", OffloadReason.TargetConflict, new DateOnly(2026, 9, 30));
        _f.QuoteItem();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.Items.Single().Outcome.Should().Be(OffloadOutcome.Moved);
        run.ExitCode.Should().Be(5, "a parked item keeps the unit failed until --retry or exclude (plan assumption A6)");
        run.ParkedTotal.Should().Be(1);
    }

    [Fact]
    public async Task Run_Success_ClearsStateAndLastRunOk()
    {
        // Arrange
        _f.State.RecordTransient(Key, OffloadReason.TransportError, 3, new DateOnly(2026, 9, 30));
        _f.QuoteItem();

        // Act
        var run = await _f.RunAsync();

        // Assert
        run.ExitCode.Should().Be(0);
        var saved = OffloadState.Load(_f.StatePaths).State!;
        saved.Items.Should().BeEmpty();
        saved.LastRun.Should().Be(new OffloadLastRun(OffloadFixture.Now, false, null));
    }

    private void Reset(bool withRoot = true)
    {
        _f.Dispose();
        _f = new OffloadFixture(withRoot);
    }

    private void Inject(string reason)
    {
        switch (reason)
        {
            case "transport_error":
                _f.Graph.Stub.OnceThrow("PUT", FileUrl, "Connection reset by peer");
                break;
            case "server_error":
                _f.Drive.RefuseNextWith(500);
                break;
            case "throttled":
                for (var i = 0; i <= Zyggy.Core.M365.Graph.GraphHttp.Retries; i++)
                {
                    _f.Graph.Stub.Once("PUT", FileUrl, 429, "{}");
                }

                break;
            default:
                _f.Graph.Stub.Once("PUT", FileUrl, 401, "{}");
                _f.Graph.Stub.Once("PUT", FileUrl, 401, "{}");
                break;
        }
    }

    private void NoGitWrite() =>
        _f.Git.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["rm", "add", "commit"]);
}
