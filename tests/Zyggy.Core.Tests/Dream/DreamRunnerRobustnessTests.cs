using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>Step 12: carried files, concurrent edits, rollup in a run, pass-through and pending-marker recovery.</summary>
public sealed class DreamRunnerRobustnessTests : IDisposable
{
    private const string StatedLine = "- [stated] 2026-09-29: Carol likes green tea.";

    private readonly MemoryTree _tree = new(
        ("private/people/_index.md", "---\nname: people\ndescription: People\nupdated: 2026-09-28\n---\n"),
        ("private/people/carol.md", "---\nname: carol\ndescription: Carol\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\n"),
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-09-28\n---\n"),
        ("business/areas/zyggy.md", "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-09-28\n---\n- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy.\n"),
        ("inbox/remember-2026-09-29.md", StatedLine + "\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private Action? _duringCall;

    public DreamRunnerRobustnessTests() =>
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _duringCall?.Invoke();
            return TestModelResults.Succeeded(TestProposals.New()
                .Disposition("L1", "filed", "private/people/carol.md")
                .Edit("private/people/carol.md", append: [StatedLine]).Build());
        });

    public void Dispose() => _tree.Dispose();

    private DreamRunner Runner()
    {
        var environment = new DreamEnvironment(_tree.Root, MemoryTree.Alice, TimeZoneInfo.Utc, Path.Combine(_tree.Root, "state"),
            Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"), null, "0.0.0-test");
        var prompts = new DreamPrompts();
        return new DreamRunner(environment, new DreamOptions(), new DreamFiler(_model, prompts, _clock), new Compressor(_model, prompts),
            new GitClient(_git, new GitClientOptions(), _clock), _clock, NullLogger<DreamRunner>.Instance);
    }

    private Task<DreamRunRecord> Run() => Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

    private string[] CommitPaths() => _git.CallsOf("commit").Single().Arguments.SkipWhile(a => a != "--").Skip(1).ToArray();

    [Fact]
    public async Task Run_CarriedDurableFileEditedByProposal_AbortsConcurrentEdit()
    {
        // Arrange
        _git.On("status", RecordingProcessRunner.Ok(" M acme/alice/private/people/carol.md\0"));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be("concurrent_edit");
        _git.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task Run_CarriedFile_CommittedAsFoundAndListed()
    {
        // Arrange
        _git.On("status", RecordingProcessRunner.Ok(" M acme/alice/business/areas/zyggy.md\0"));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        record.Carried.Should().Equal("business/areas/zyggy.md");
        CommitPaths().Should().Contain("acme/alice/business/areas/zyggy.md");
        _git.CallsOf("commit").Single().StandardInput.Should().Contain("carried: 1");
    }

    [Fact]
    public async Task Run_DurableFileChangedAfterSnapshot_AbortsConcurrentEdit()
    {
        // Arrange
        _duringCall = () => File.AppendAllText(_tree.Full("private/people/carol.md"), "- [stated] 2026-10-04: typed by the owner meanwhile.\n");

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be("concurrent_edit");
        File.ReadAllText(_tree.Full("private/people/carol.md")).Should().Contain("typed by the owner meanwhile").And.NotContain("green tea");
    }

    [Fact]
    public async Task Run_InboxNeverInCommitPaths()
    {
        // Arrange: a closed, consumed, quiet inbox file is deleted by the rollup.
        var old = "- [stated] 2026-09-01: old fact.";
        _tree.Write("inbox/remember-2026-09-01.md", old + "\n");
        File.SetLastWriteTimeUtc(_tree.Full("inbox/remember-2026-09-01.md"), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        var ledger = DreamLedger.Empty();
        ledger.Consume("inbox/remember-2026-09-01.md", [LineHash.Of(old)], new DateOnly(2026, 9, 20));
        _tree.Write(".dream/ledger.json", ledger.Serialize());

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        record.Rollup.InboxDeleted.Should().Be(1);
        File.Exists(_tree.Full("inbox/remember-2026-09-01.md")).Should().BeFalse();
        CommitPaths().Should().NotContain(p => p.Contains("/inbox/", StringComparison.Ordinal));
        DreamLedger.Load(File.ReadAllText(_tree.Paths.Ledger)).Ledger!.Files.Should().NotContain("inbox/remember-2026-09-01.md");
    }

    [Fact]
    public async Task Run_AutoAndDailyChanges_PassedThroughAndSecretOneWithheld()
    {
        // Arrange
        _tree.Write("auto/MEMORY.md", "# auto\n");
        _tree.Write("auto/notes.md", "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\n");
        _git.On("status", RecordingProcessRunner.Ok(), RecordingProcessRunner.Ok(" M acme/alice/auto/MEMORY.md\0?? acme/alice/auto/notes.md\0"));

        // Act
        var record = await Run();

        // Assert
        record.Withheld.Should().Equal("auto/notes.md");
        CommitPaths().Should().Contain("acme/alice/auto/MEMORY.md").And.NotContain("acme/alice/auto/notes.md");
        _git.CallsOf("add").Single().Arguments.Should().NotContain("acme/alice/auto/MEMORY.md", "a tracked file is not added");
    }

    [Fact]
    public async Task Run_PendingMarkerFromDeadRun_RestoredThenRunContinues()
    {
        // Arrange: a dead run wrote carol.md and a new file, then died before its commit.
        var carol = _tree.Full("private/people/carol.md");
        File.AppendAllText(carol, "- [stated] 2026-09-29: written by the dead run.\n");
        _tree.Write("business/areas/new.md", "- [observed] 2026-09-29 [x 2026-09-29]: dead run.\n");
        var marker = new JsonObject
        {
            ["run"] = "01JDEAD",
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = "private/people/carol.md", ["sha256_written"] = MemorySnapshot.Hash(File.ReadAllBytes(carol)), ["existed_before"] = true, ["sha256_before"] = new string('a', 64) },
                new JsonObject { ["path"] = "business/areas/new.md", ["sha256_written"] = MemorySnapshot.Hash(File.ReadAllBytes(_tree.Full("business/areas/new.md"))), ["existed_before"] = false, ["sha256_before"] = null }),
        };
        _tree.Write(".dream/pending.json", marker.ToJsonString());
        _git.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "checkout")
            {
                File.WriteAllText(carol, "---\nname: carol\ndescription: Carol\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\n");
            }

            return null;
        };

        // Act
        var record = await Run();

        // Assert
        _git.CallsOf("checkout").Single().Arguments.Should().Equal("checkout", "HEAD", "--", "acme/alice/private/people/carol.md");
        File.Exists(_tree.Full("business/areas/new.md")).Should().BeFalse();
        record.Outcome.Should().Be("committed");
        File.ReadAllText(carol).Should().NotContain("dead run").And.Contain("green tea");
        File.Exists(_tree.Paths.Pending).Should().BeFalse();
    }

    [Fact]
    public async Task Run_PendingPathEditedSince_AbortsDirtyPending()
    {
        // Arrange
        var marker = new JsonObject
        {
            ["run"] = "01JDEAD",
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = "private/people/carol.md",
                ["sha256_written"] = new string('c', 64),
                ["existed_before"] = true,
                ["sha256_before"] = new string('a', 64),
            }),
        };
        _tree.Write(".dream/pending.json", marker.ToJsonString());

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be("dirty_pending");
        File.Exists(_tree.Paths.Pending).Should().BeTrue();
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }
}
