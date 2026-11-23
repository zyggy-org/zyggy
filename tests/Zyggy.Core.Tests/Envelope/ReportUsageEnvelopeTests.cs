using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 60 AC-4: the optional report fields <c>max_turns</c>, <c>cache_write_tokens</c> and <c>cache_read_tokens</c> — typed parse of the
/// hand-derived golden <c>report-usage</c>, the factory signing to its exact bytes, and the parse-time refusal of a bad value.
/// </summary>
public sealed class ReportUsageEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    // AC-4: the golden reads back every turn and token count.
    [Fact]
    public void Parse_GoldenReportUsage_YieldsTurnsLimitAndFourTokenCounts()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("report-usage"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        ReportFields report = result.Envelope!.Report!;
        new { report.NumTurns, report.MaxTurns, report.InputTokens, report.OutputTokens, report.CacheWriteTokens, report.CacheReadTokens, report.CostUsd }
            .Should().BeEquivalentTo(new { NumTurns = 57, MaxTurns = 60, InputTokens = 66, OutputTokens = 626, CacheWriteTokens = 25000, CacheReadTokens = 49279, CostUsd = 1.85m });
    }

    // AC-4: a report without the new fields reads them as absent (the existing golden, unchanged).
    [Fact]
    public void Parse_GoldenReportWithoutUsageFields_NewFieldsNull()
    {
        // Act
        ReportFields report = EnvelopeParser.Parse(Golden.Md("report"), Acme).Envelope!.Report!;

        // Assert
        new { report.MaxTurns, report.CacheWriteTokens, report.CacheReadTokens }
            .Should().BeEquivalentTo(new { MaxTurns = (int?)null, CacheWriteTokens = (int?)null, CacheReadTokens = (int?)null });
    }

    // AC-4: the factory writes the three fields, and the signed bytes equal the hand-derived golden.
    [Fact]
    public async Task CreateReport_WithTurnsAndTokens_SignsToTheGoldenBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var header = new EnvelopeHeader(
            Acme,
            EnvelopeId.Parse("01M8479QSJBT20P911ESXFYM90"),
            MachineName.Parse("home-laptop"),
            MachineName.Parse("central"),
            new DateTimeOffset(2026, 11, 23, 10, 5, 0, TimeSpan.Zero),
            EnvelopePriority.Normal,
            EnvelopeId.Parse("01M8479QSJBT20P911ESXFYM8Z"));
        var fields = new ReportFields(
            ReportStatus.Done,
            Started: new DateTimeOffset(2026, 11, 23, 9, 40, 0, TimeSpan.Zero),
            Finished: new DateTimeOffset(2026, 11, 23, 10, 4, 30, TimeSpan.Zero),
            CostUsd: 1.85m,
            InputTokens: 66,
            OutputTokens: 626,
            NumTurns: 57,
            Model: "test-model-1",
            MaxTurns: 60,
            CacheWriteTokens: 25000,
            CacheReadTokens: 49279);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        Model created = Model.CreateReport(header, fields, "## REPORT\nsummary: The benchmark run finished within its turn limit.\n"u8.ToArray());
        EnvelopeResult signed = await signer.SignAsync(created, TestKeys.Acme1, ct);

        // Assert
        EnvelopeWriter.Serialize(signed.Envelope!).Should().Equal(Golden.Md("report-usage"));
    }

    // AC-4: a negative count or a zero limit is refused at parse time.
    [Theory]
    [InlineData("max_turns: 60", "max_turns: 0", "max_turns")]
    [InlineData("cache_write_tokens: 25000", "cache_write_tokens: -1", "cache_write_tokens")]
    [InlineData("cache_read_tokens: 49279", "cache_read_tokens: x", "cache_read_tokens")]
    public void Parse_BadUsageValue_RejectedNamingTheField(string from, string to, string field)
    {
        // Arrange
        byte[] md = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Golden.Md("report-usage")).Replace(from, to, StringComparison.Ordinal));

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(md, Acme);

        // Assert
        result.Rejection.Should().NotBeNull();
        result.Rejection!.ToString().Should().Contain(field);
    }
}
