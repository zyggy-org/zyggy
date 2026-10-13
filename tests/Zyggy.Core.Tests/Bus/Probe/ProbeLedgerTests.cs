using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus.Probe;

/// <summary>Spec 04: the sender's append-only JSONL ledger.</summary>
public sealed class ProbeLedgerTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task AppendAsync_TwoRecords_TwoJsonLinesWithWireFields()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var ledger = new ProbeLedger(Path.Combine(_dir, "probe", "sent.jsonl"));
        var done = new ProbeRecord(EnvelopeId.New(T), MachineName.Parse("home-laptop"), T, T.AddMilliseconds(1234), ProbeOutcome.Done, null, 1234);
        var timeout = new ProbeRecord(EnvelopeId.New(T.AddSeconds(1)), MachineName.Parse("work-laptop"), T.AddSeconds(1), null, ProbeOutcome.Timeout, null, null);

        // Act
        await ledger.AppendAsync(done, ct);
        await ledger.AppendAsync(timeout, ct);

        // Assert
        string[] lines = (await File.ReadAllTextAsync(ledger.Path, ct)).Split('\n');
        lines.Should().HaveCount(3);
        lines[2].Should().BeEmpty();
        lines[0].Should().Contain("\"outcome\":\"done\"").And.Contain("\"roundTripMs\":1234").And.Contain("\"to\":\"home-laptop\"").And.Contain("\"sentUtc\":\"2026-10-12T09:00:00.000Z\"");
        lines[1].Should().Contain("\"outcome\":\"timeout\"").And.NotContain("roundTripMs").And.NotContain("observedUtc").And.NotContain("reason");
    }

    [Fact]
    public async Task ReadAll_RoundTrips()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var ledger = new ProbeLedger(Path.Combine(_dir, "sent.jsonl"));
        var records = new[]
        {
            new ProbeRecord(EnvelopeId.New(T), MachineName.Parse("home-laptop"), T, T.AddMilliseconds(500), ProbeOutcome.Done, null, 500),
            new ProbeRecord(EnvelopeId.New(T), MachineName.Parse("home-laptop"), T, T.AddSeconds(2), ProbeOutcome.Rejected, "wrong_recipient", null),
            new ProbeRecord(EnvelopeId.New(T), MachineName.Parse("work-laptop"), T, null, ProbeOutcome.PushFailed, "fetch first", null),
        };
        foreach (ProbeRecord record in records)
        {
            await ledger.AppendAsync(record, ct);
        }

        // Act
        IReadOnlyList<ProbeRecord> back = ledger.ReadAll();

        // Assert
        back.Should().Equal(records);
    }

    [Fact]
    public void ReadAll_MissingFile_Empty()
    {
        // Act
        IReadOnlyList<ProbeRecord> back = new ProbeLedger(Path.Combine(_dir, "absent.jsonl")).ReadAll();

        // Assert
        back.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAll_GarbageLine_Skipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "sent.jsonl");
        var ledger = new ProbeLedger(path);
        await ledger.AppendAsync(new ProbeRecord(EnvelopeId.New(T), MachineName.Parse("home-laptop"), T, null, ProbeOutcome.Timeout, null, null), ct);
        await File.AppendAllTextAsync(path, "not json\n{\"id\":\"bad\"}\n", ct);

        // Act
        IReadOnlyList<ProbeRecord> back = ledger.ReadAll();

        // Assert
        back.Should().ContainSingle();
    }

    [Fact]
    public void DefaultPath_UnderLocalAppDataProbeTenant()
    {
        // Act
        string path = ProbeLedger.DefaultPath("/local", TenantId.Parse("acme"));

        // Assert
        path.Should().Be(Path.Join("/local", "zyggy", "probe", "acme", "sent.jsonl"));
    }
}
