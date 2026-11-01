using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeParserGoldenTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Theory]
    [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]
    public void Parse_EveryGoldenCase_IsAccepted(string name)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md(name), Acme);

        // Assert
        result.Rejection.Should().BeNull();
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Parse_GoldenJob_YieldsTypedJobFields()
    {
        // Act
        Model envelope = Accept("job");

        // Assert
        envelope.Schema.Should().Be(1);
        envelope.Type.Should().Be(EnvelopeType.Job);
        envelope.Header.Tenant.Should().Be(Acme);
        envelope.Header.Id.Value.Should().Be("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");
        envelope.Header.From.Value.Should().Be("central");
        envelope.Header.To.Value.Should().Be("home-laptop");
        envelope.Header.Priority.Should().Be(EnvelopePriority.Normal);
        envelope.Header.Created.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero));
        envelope.Header.InReplyTo.Should().BeNull();
        envelope.Job.Should().NotBeNull();
        envelope.Job!.Project.Should().Be("calizr");
        envelope.Job.Agent.Should().Be("env-debugger");
        envelope.Job.Worktree.Should().BeTrue();
        envelope.Job.AllowedTools.Should().Equal("Read", "Grep", "Glob", "Bash(dotnet *)", "Bash(kubectl get *)");
        envelope.Job.ReportBack.Should().Equal("summary", "diff", "files_changed");
        envelope.Job.TimeoutMinutes.Should().Be(30);
        envelope.Job.Attempt.Should().Be(1);
        envelope.Job.Deadline.Should().Be(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
        envelope.KeyId.Should().Be(KeyId.Parse("acme/1"));
        envelope.Signature.Should().Be("hmac-sha256:" + Golden.Sig("job"));
        envelope.Body.ToArray().Should().Equal(Encoding.UTF8.GetBytes(
            "Investigate why staging returns HTTP 502 on /api/bookings since Friday.\nDo not change code; report root cause and a proposed fix.\n"));
        envelope.BodyText.Should().StartWith("Investigate why staging");
        envelope.Report.Should().BeNull();
        envelope.Context.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenReport_YieldsTypedReportFields()
    {
        // Act
        Model envelope = Accept("report");

        // Assert
        envelope.Type.Should().Be(EnvelopeType.Report);
        envelope.Header.InReplyTo.Should().Be(EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0"));
        envelope.Report.Should().NotBeNull();
        envelope.Report!.Status.Should().Be(ReportStatus.Failed);
        envelope.Report.Reason.Should().Be("timeout");
        envelope.Report.NumTurns.Should().Be(7);
        envelope.Report.CostUsd.Should().Be(0.42m);
        envelope.Report.DurationSeconds.Should().Be(1830);
        envelope.Report.InputTokens.Should().Be(12345);
        envelope.Report.OutputTokens.Should().Be(2345);
        envelope.Report.Model.Should().Be("test-model-1");
        envelope.Report.FilesChanged.Should().Equal("src/Api/Bookings.cs", "README.md");
        envelope.Report.DiffRef.Should().Be("3f2a9c1");
        envelope.Report.Started.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 9, 0, TimeSpan.Zero));
        envelope.Report.Finished.Should().Be(new DateTimeOffset(2026, 9, 27, 14, 39, 30, TimeSpan.Zero));
        envelope.Job.Should().BeNull();
        envelope.Context.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenContext_YieldsScope()
    {
        // Act
        Model envelope = Accept("context");

        // Assert
        envelope.Type.Should().Be(EnvelopeType.Context);
        envelope.Context!.Scope.Should().Be(new ContextScope(ContextScopeKind.Project, "calizr"));
        envelope.KeyId.Should().Be(KeyId.Parse("acme/2"));
        envelope.Job.Should().BeNull();
        envelope.Report.Should().BeNull();
    }

    [Fact]
    public void Parse_GoldenJobNoncanonical_KeepsUnknownFieldsAndParsedText()
    {
        // Act
        Model envelope = Accept("job-noncanonical");

        // Assert
        IReadOnlyDictionary<string, FrontMatterNode> entries = envelope.FrontMatter.Entries;
        entries["x_experimental"].Should().Be(new FrontMatterScalar("a: b"));
        entries["x_empty"].Should().Be(new FrontMatterScalar(""));
        entries["x_quote"].Should().Be(new FrontMatterScalar("it's: here"));
        entries["x_null"].Should().Be(new FrontMatterScalar("null"));
        entries["deadline"].Should().Be(new FrontMatterScalar("2026-09-29T18:00:00.500+02:00"));
        FrontMatterMapping meta = entries["x_meta"].Should().BeOfType<FrontMatterMapping>().Subject;
        FrontMatterSequence steps = meta.Entries["steps"].Should().BeOfType<FrontMatterSequence>().Subject;
        steps.Items.Should().HaveCount(2).And.AllBeOfType<FrontMatterMapping>();
        meta.Entries.Keys.Should().Equal("owner", "steps", "tags");
        envelope.Job!.Worktree.Should().BeTrue();
        envelope.Job.AllowedTools.Should().Equal("Read", "Bash(dotnet *)");
        envelope.Job.Deadline.Should().Be(new DateTimeOffset(2026, 9, 29, 16, 0, 0, 500, TimeSpan.Zero));
        envelope.Job.Deadline!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Parse_GoldenContextCrlfBody_BodyIsByteExactAndFrontMatterEndedAtFirstDelimiter()
    {
        // Arrange
        byte[] expectedBody = Encoding.UTF8.GetBytes("[observed] line one\r\n---\r\n[observed] line three");

        // Act
        Model envelope = Accept("context-crlf-body");

        // Assert
        envelope.Body.ToArray().Should().Equal(expectedBody);
        envelope.Context.Should().NotBeNull();
        envelope.Context!.Scope.Kind.Should().Be(ContextScopeKind.Machine);
    }

    /// <summary>
    /// A golden item changed after signing so that the parser still accepts it and only the signature catches it (spec 41 AC-6).
    /// The replacement digest of the body tamper (<c>Quote</c> → <c>Uuote</c>) was computed with <c>sha256sum</c>, never by the code under test.
    /// </summary>
    public static TheoryData<string, byte[]> ItemTampers => new()
    {
        { "item-onedrive size", EnvelopeText.From(Golden.Md("item-onedrive")).WithValue("size", "69").ToBytes() },
        {
            "item-onedrive sha256",
            EnvelopeText.From(Golden.Md("item-onedrive")).WithValue("sha256", "9f21e3a595dcaa1a4e89d7006d10073d5099025827f05a1b84ac475028621650").ToBytes()
        },
        {
            "item-onedrive drive_name and slug",
            EnvelopeText.From(Golden.Md("item-onedrive")).WithValue("slug", "other-notes").WithValue("drive_name", "other-notes.txt").ToBytes()
        },
        {
            "item-bus one body character with matching sha256",
            EnvelopeText.From(Golden.Md("item-bus"))
                .WithValue("sha256", "9f21e3a595dcaa1a4e89d7006d10073d5099025827f05a1b84ac475028621650")
                .WithBody(Encoding.ASCII.GetBytes("VXVvdGUgZm9yIHRoZSBaeWdneSByb29mIHJlcGFpci4KVG90YWwgMSAyMzQsMDAgRVVSLCB2YWxp\nZCAzMCBkYXlzLgo=\n"))
                .ToBytes()
        },
    };

    [Theory]
    [InlineData("item-bus")]
    [InlineData("item-bus-png")]
    [InlineData("item-onedrive")]
    public async Task ItemGoldens_CanonicalBytesAndSignatureEqualTheFiles(string name)
    {
        // Arrange
        Model item = Accept(name);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(item);
        EnvelopeResult signed = await signer.SignAsync(item, TestKeys.Acme1, TestContext.Current.CancellationToken);

        // Assert
        item.Type.Should().Be(EnvelopeType.Item);
        canonical.Should().Equal(Golden.Canonical(name));
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig(name));
    }

    [Theory]
    [MemberData(nameof(ItemTampers))]
    public async Task ItemGolden_OneByteOrFieldChangedAfterSigning_InvalidSignature(string because, byte[] file)
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));
        EnvelopeParser.Parse(file, Acme).IsAccepted.Should().BeTrue(because);

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, TestContext.Current.CancellationToken);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature, because);
    }

    [Theory]
    [InlineData("job.md", "b3c63c24dd41142c5400f84b5146132c0925e64144e0391083de0eef6eda3820")]
    [InlineData("job.canonical", "251a0cf09b92b8801287184790461370b82b3a20a73f33fd8d18e3e0666c1a46")]
    [InlineData("job.sig", "2321393db4195576238ce5665d2d2a89964634a67e25fff0a5b1930fa1e19c17")]
    [InlineData("job-noncanonical.md", "a65c994bde0be7945d1e0795c3cf0f7ab5c89f4cf616a8e9ded16bb29287ca10")]
    [InlineData("job-noncanonical.canonical", "336d937d6987f118979c1f18890e0e3efd64d8bdf1afc9f84d3e6e038df6b893")]
    [InlineData("job-noncanonical.sig", "14f77d2ac2fe509372a653eaf8dd01d38e9ea51619321997000c6521f1b4d610")]
    [InlineData("report.md", "3ede1287f48d88a3479a1ff56edabb76311578d7cff4989c8d7f90dfe88fc85b")]
    [InlineData("report.canonical", "b7c65abcdc82a125371fa9f7d3f1871026bd85a132d9a9c06de54a9285d1daf7")]
    [InlineData("report.sig", "3e6df1aa50dbb03b00bc5718a924d9beb6325246fde3b724083079dcc35a9f24")]
    [InlineData("context.md", "b4cff9d0da31c9515cc3dc5e01b8819ac2d489a5d1c19563f87e7d0fe115b2c4")]
    [InlineData("context.canonical", "6e205931ac1f9b0f31750043a1cef85451278ee31ecb1b9969c38fbea82f9350")]
    [InlineData("context.sig", "38332c3924b15aa6bfd1d7bbcf3268813945c92e8d7c91330540679ec125371b")]
    [InlineData("context-crlf-body.md", "e272c3015f974ee9ed8d0cd798bc245786cf3bedad8b01c2d4f2799a15ef2189")]
    [InlineData("context-crlf-body.canonical", "9d64c525cc5c9540d9a6a0fb08a677e76b17eb12678a9fc16c37c9bfde0a2b3e")]
    [InlineData("context-crlf-body.sig", "64e3ab4758e3b27c1ef8341476e1ea1b7f27b927e5c86b73ac0feccf452654fc")]
    public void ExistingGoldens_Unchanged(string file, string sha256)
    {
        // Act: the digests were taken with sha256sum before deliverable 41 added the item cases
        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Golden.Directory, file))));

        // Assert
        actual.Should().Be(sha256);
    }

    private static Model Accept(string name)
    {
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md(name), Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }
}
