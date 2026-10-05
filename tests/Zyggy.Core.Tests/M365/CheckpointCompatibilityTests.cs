using Zyggy.Core.M365.Runs;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// Spec 33 AC-33: the checkpoint and watermark files the shell left on Central are read as they are and the backfill continues where it
/// stopped; only the fields the scripts wrote change.
/// </summary>
public sealed class CheckpointCompatibilityTests : IDisposable
{
    private readonly RunFixture _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public async Task Mail_ShellCheckpoint_FinishedSkippedUnfinishedResumesPrintsResumingUnknownKept()
    {
        // Arrange: jq -c output of mail-backfill.sh — Inbox finished, Sent Items two batches in, plus a field the scripts never wrote
        File.WriteAllText(Path.Combine(_run.State, "mail-backfill.json"),
            """{"folders":{"AQMkInbox0001":{"name":"Inbox","watermark":"2026-01-01T00:00:00Z","done":true,"batches":7,"messages":150,"facts":40,"duplicates":3,"refused":1,"turns":30,"cost":1.2},"AQMkSentItems0001":{"name":"Sent Items","watermark":"2026-07-01T00:00:00Z","done":false,"batches":2,"messages":50,"facts":12,"duplicates":0,"refused":0,"turns":8,"cost":0.3}},"total_messages":200,"total_facts":52,"total_duplicates":3,"total_refused":1,"total_batches":9,"total_turns":38,"total_cost":1.5,"started":"2026-09-20T08:00:00Z","updated":"2026-09-21T08:00:00Z","x_owner_note":"kept"}""" + "\n");
        File.WriteAllText(Path.Combine(_run.State, "backfill-AQMkSentItems0001.watermark"), "2026-07-01T00:00:00Z\n");
        _run.Acts((_, _) => "mail-backfill batch: messages 0, facts 0 (0 dup, 0 refused)");

        // Act
        var outcome = await new MailBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock).RunAsync(null, false, TestContext.Current.CancellationToken);

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines.Should().Contain("mail-backfill: resuming folder Sent Items from 2026-07-01T00:00:00Z");
        _run.Requests.Should().NotContain(r => r.Prompt.Contains("AQMkInbox0001", StringComparison.Ordinal));
        var checkpoint = _run.Checkpoint("mail-backfill.json");
        checkpoint["x_owner_note"]!.GetValue<string>().Should().Be("kept");
        checkpoint["started"]!.GetValue<string>().Should().Be("2026-09-20T08:00:00Z");
        checkpoint["folders"]!["AQMkInbox0001"]!["cost"]!.ToJsonString().Should().Be("1.2");
        checkpoint["total_batches"]!.GetValue<double>().Should().Be(11);
        File.ReadAllText(Path.Combine(_run.State, "mail-backfill.json")).Should().EndWith("}\n").And.NotContain("\n{");
    }

    [Fact]
    public async Task Files_ShellCheckpointAndPlainIsoWatermark_Resumes()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_run.State, "files-backfill.json"),
            """{"drives":{"b!onedrive0001":{"name":"OneDrive","site":"onedrive","watermark":"2026-09-02T08:00:00Z","done":false,"forbidden":false,"batches":3,"listed":14,"parsed":12,"skipped":2,"skipped_by":{"type":1,"size":1,"path":0,"parse_error":0,"secret_pattern":0},"facts":20,"duplicates":0,"refused":0,"turns":12,"cost":0.6}},"total_listed":14,"total_parsed":12,"total_skipped":2,"total_skipped_by":{"type":1,"size":1,"path":0,"parse_error":0,"secret_pattern":0},"total_facts":20,"total_duplicates":0,"total_refused":0,"total_batches":3,"total_turns":12,"total_cost":0.6,"started":"2026-09-20T08:00:00Z","updated":"2026-09-21T08:00:00Z"}""" + "\n");
        File.WriteAllText(Path.Combine(_run.State, "files-backfill-b!onedrive0001.watermark"), "2026-09-02T08:00:00Z\n");
        _run.Acts((r, _) =>
        {
            var n = r.Prompt.Split('\n')[0].Split(' ')[^1];
            return $"files-backfill batch: listed {n}, parsed {n}, skipped 0 (type 0, size 0, path 0, parse error 0, secret pattern 0), facts 1 (0 dup, 0 refused)";
        });

        // Act
        var outcome = await new FilesBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock).RunAsync("OneDrive", false, TestContext.Current.CancellationToken);

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines[0].Should().Be("files-backfill: resuming drive OneDrive from 2026-09-02T08:00:00Z");
        _run.Requests[0].Prompt.Split('\n')[2].Should().StartWith("01F14\t");
    }

    [Theory]
    [InlineData("mail-backfill.json", "[]")]
    [InlineData("mail-backfill.json", "{\"folders\":[]}")]
    [InlineData("mail-backfill.json", "not json")]
    public async Task NotACheckpoint_ExitThreeNamesReset(string file, string content)
    {
        // Arrange
        var path = Path.Combine(_run.State, file);
        File.WriteAllText(path, content);
        _run.Acts((_, _) => string.Empty);

        // Act
        var outcome = await new MailBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock).RunAsync(null, false, TestContext.Current.CancellationToken);

        // Assert
        outcome.Exit.Should().Be(3);
        outcome.StderrLines[^1].Should().Be($"mail-backfill: configuration error: {path} is not a checkpoint (zyggy m365 mail-backfill --reset starts again)");
        _run.Requests.Should().BeEmpty();
    }
}
