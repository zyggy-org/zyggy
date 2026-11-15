using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 18 "Questions and decisions": Central's decisions are archived after 14 days by the sweep, like reports — and only from
/// Central's own <c>decisions/</c> folder. Through the git and bus seams. Now is 2026-11-14 12:00 UTC.
/// </summary>
public sealed class SweepDecisionArchiveTests : IDisposable
{
    private static readonly TenantId Acme = SweepClaimsHarness.Acme;

    private static readonly MachineName Central = SweepClaimsHarness.Central;

    private static readonly MachineName HomeLaptop = SweepClaimsHarness.HomeLaptop;

    private static readonly DateTimeOffset Now = SweepClaimsHarness.Now;

    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task RunAsync_DecisionOlderThanFourteenDays_ArchivedByCreatedMonth_WithTheOldReportInOneCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset twenty = Now.AddDays(-20);
        DateTimeOffset fifteen = Now.AddDays(-15);
        (string decision, EnvelopeId decisionId) = await DropDecisionAsync(Central, twenty, ct);
        EnvelopeId reportId = EnvelopeId.New(fifteen);
        string report = BusPaths.EnvelopeFile(BusPaths.Reports(Acme, Central), reportId);
        _h.Checkout.Drop(report, await TestEnvelopes.SignedReportBytesAsync(Central, HomeLaptop, EnvelopeId.New(fifteen), await _h.StoreAsync(ct), TestKeys.Acme1, "note body\n", ct, id: reportId, created: fifteen));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(2);
        result.OldestArchived.Should().Be(twenty);
        _h.Processes.CallsOf("mv").Select(c => (c.Arguments[2], c.Arguments[3])).Should().BeEquivalentTo(
            [(decision, Archived(decisionId, twenty)), (report, Archived(reportId, fifteen))]);
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith("acme archive 2 central→archive");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task RunAsync_DecisionAtOrPastFourteenDays_ArchivedOnlyWhenStrictlyOlder(int secondsPast, int expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropDecisionAsync(Central, Now.AddDays(-14).AddSeconds(-secondsPast), ct);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(expected);
        _h.Processes.CallsOf("mv").Should().HaveCount(expected);
    }

    [Fact]
    public async Task RunAsync_OldDecisionInALaptopsFolderAndOldQuestion_NeverArchived()
    {
        // Arrange: a decision where none belongs is the auditor's business; an unanswered question stays open whatever its age
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset old = Now.AddDays(-60);
        await DropDecisionAsync(HomeLaptop, old, ct);
        EnvelopeId questionId = EnvelopeId.New(old);
        Model question = Model.CreateQuestion(
            new EnvelopeHeader(Acme, questionId, HomeLaptop, Central, old),
            new QuestionFields("calizr", "keep the name central", [], "5b3fd3ac"),
            Encoding.UTF8.GetBytes("Which role name?\n"));
        _h.Checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), questionId), await SignedAsync(question, ct));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Should().BeEquivalentTo(new SweepResult([], null, false));
        _h.Processes.Calls.Should().BeEmpty();
    }

    private static string Archived(EnvelopeId id, DateTimeOffset created) =>
        BusPaths.EnvelopeFile(BusPaths.Archive(Acme, created.UtcDateTime.Year, created.UtcDateTime.Month), id);

    /// <summary>A signed decision of Central dropped in <paramref name="folder"/>'s <c>decisions/</c> folder.</summary>
    private async Task<(string Path, EnvelopeId Id)> DropDecisionAsync(MachineName folder, DateTimeOffset created, CancellationToken ct)
    {
        EnvelopeId id = EnvelopeId.New(created);
        Model decision = Model.CreateDecision(
            new EnvelopeHeader(Acme, id, Central, HomeLaptop, created, EnvelopePriority.Normal, EnvelopeId.New(created.AddMinutes(-1))),
            new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"),
            Encoding.UTF8.GetBytes("Use central.\n"));
        string path = BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, folder), id);
        _h.Checkout.Drop(path, await SignedAsync(decision, ct));
        return (path, id);
    }

    private async Task<byte[]> SignedAsync(Model envelope, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(await _h.StoreAsync(ct)).SignAsync(envelope, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }
}
