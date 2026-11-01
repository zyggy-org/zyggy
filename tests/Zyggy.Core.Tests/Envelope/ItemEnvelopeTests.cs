using System.Security.Cryptography;
using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>The <c>item</c> envelope of deliverable 41 (spec 41 AC-1..AC-4, AC-7; founding spec §4 W41-6).</summary>
public sealed class ItemEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly byte[] TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n"u8.ToArray();

    private static readonly byte[] TamperedTextItem = "Uuote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static byte[] SampleText => [.. TextItem];

    private static EnvelopeText Bus => EnvelopeText.From(Golden.Md("item-bus"));

    private static EnvelopeText OneDrive => EnvelopeText.From(Golden.Md("item-onedrive"));

    public static TheoryData<string> MandatoryFields => new() { "via", "project", "slug", "name", "description", "source_name", "size", "sha256" };

    /// <summary>A golden file with one field changed, added or removed, and the field the parser must name.</summary>
    public static TheoryData<string, byte[], string> InvalidFields => new()
    {
        { "project not a slug", Bus.WithValue("project", "Calizr").ToBytes(), "project" },
        { "slug not a slug", Bus.WithValue("slug", "-x").ToBytes(), "slug" },
        { "name over 100", Bus.WithValue("name", new string('n', 101)).ToBytes(), "name" },
        { "name with a line break", Bus.WithValue("name", "\"Staging\\nrestore\"").ToBytes(), "name" },
        { "description over 149", Bus.WithValue("description", new string('d', 150)).ToBytes(), "description" },
        { "source_name over 100", Bus.WithValue("source_name", new string('s', 101)).ToBytes(), "source_name" },
        { "source_name with a control character", Bus.WithValue("source_name", "\"restore\\anotes.md\"").ToBytes(), "source_name" },
        { "size zero", Bus.WithValue("size", "0").ToBytes(), "size" },
        { "size negative", Bus.WithValue("size", "-1").ToBytes(), "size" },
        { "size fractional", Bus.WithValue("size", "1.5").ToBytes(), "size" },
        { "size leading zero", Bus.WithValue("size", "01").ToBytes(), "size" },
        { "sha256 uppercase", Bus.WithValue("sha256", "C5F2CAB5D3B4D46C44BEE6F1EE037D471C419B739908F4A05251745FB4794F88").ToBytes(), "sha256" },
        { "sha256 63 characters", Bus.WithValue("sha256", "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f8").ToBytes(), "sha256" },
        { "via unknown", Bus.WithValue("via", "drive").ToBytes(), "via" },
        { "drive_name with via bus", Bus.WithKey("drive_name", "staging-restore-notes.txt").ToBytes(), "drive_name" },
        { "drive_name absent with via onedrive", OneDrive.WithoutKey("drive_name").ToBytes(), "drive_name" },
        { "drive_name stem not the slug", OneDrive.WithValue("drive_name", "other-slug.txt").ToBytes(), "drive_name" },
        { "drive_name md is not an item extension", OneDrive.WithValue("drive_name", "staging-restore-notes.md").ToBytes(), "drive_name" },
        { "drive_name exe", OneDrive.WithValue("drive_name", "staging-restore-notes.exe").ToBytes(), "drive_name" },
    };

    /// <summary>A bus item whose body breaks the W41-6 body rule (AC-3).</summary>
    public static TheoryData<string, byte[]> BadBodies => new()
    {
        { "character outside the alphabet", Bus.WithBody(Ascii("*XVvdGUgZm9yIHRoZSBaeWdneSByb29mIHJlcGFpci4KVG90YWwgMSAyMzQsMDAgRVVSLCB2YWxp\nZCAzMCBkYXlzLgo=\n")).ToBytes() },
        { "77-character line before the last", Bus.WithBody(Ascii("UXVvdGUgZm9yIHRoZSBaeWdneSByb29mIHJlcGFpci4KVG90YWwgMSAyMzQsMDAgRVVSLCB2YWxpZ\nCAzMCBkYXlzLgo=\n")).ToBytes() },
        { "decodes to size - 1", Bus.WithBody(ItemBody.Encode(TextItem.AsSpan(0, TextItem.Length - 1))).ToBytes() },
        { "digest differs", Bus.WithBody(ItemBody.Encode(TamperedTextItem)).ToBytes() },
    };

    [Fact]
    public async Task CreateItem_BusRoute_SignSerialiseParseVerify_EveryFieldEqual_BodyDecodesToTheBytes_SchemaOne_OnlySection4Keys()
    {
        // Arrange
        ItemFields fields = Fields(ItemRoute.Bus, driveName: null);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        Model reparsed = await RoundTripAsync(signer, Model.CreateItem(Header(), fields, TextItem));

        // Assert
        reparsed.Type.Should().Be(EnvelopeType.Item);
        reparsed.Schema.Should().Be(1);
        reparsed.Item.Should().Be(fields);
        reparsed.Item!.DriveName.Should().BeNull();
        ItemBody.TryDecode(reparsed, out byte[]? decoded).Should().BeTrue();
        decoded.Should().Equal(TextItem);
        reparsed.Job.Should().BeNull();
        reparsed.Report.Should().BeNull();
        reparsed.Context.Should().BeNull();
        reparsed.FrontMatter.Entries.Keys.Should().BeEquivalentTo(
            "schema", "id", "tenant", "type", "from", "to", "created", "priority",
            "via", "project", "slug", "name", "description", "source_name", "size", "sha256", "key_id", "sig");
    }

    [Fact]
    public async Task CreateItem_OneDriveRoute_DriveNamePresent_EmptyBody()
    {
        // Arrange
        ItemFields fields = Fields(ItemRoute.OneDrive, driveName: "staging-restore-notes.txt");
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        Model reparsed = await RoundTripAsync(signer, Model.CreateItem(Header(), fields, ReadOnlyMemory<byte>.Empty));

        // Assert
        reparsed.Item.Should().Be(fields);
        reparsed.Body.Length.Should().Be(0);
        reparsed.FrontMatter.Entries.Keys.Should().BeEquivalentTo(
            "schema", "id", "tenant", "type", "from", "to", "created", "priority",
            "via", "project", "slug", "drive_name", "name", "description", "source_name", "size", "sha256", "key_id", "sig");
    }

    [Fact]
    public void CreateItem_OneDriveRouteWithFileBytes_ThrowsArgumentException()
    {
        // Arrange
        Action act = () => Model.CreateItem(Header(), Fields(ItemRoute.OneDrive, "staging-restore-notes.txt"), TextItem);

        // Act & Assert
        act.Should().Throw<ArgumentException>().WithParameterName("fileBytes");
    }

    [Theory]
    [MemberData(nameof(MandatoryFields))]
    public void Parse_MissingMandatoryField_MissingFieldNamesIt(string key)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Bus.WithoutKey(key).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void Parse_InvalidField_InvalidFieldNamesIt(string because, byte[] file, string field)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);

        // Assert
        result.IsAccepted.Should().BeFalse(because);
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField, because);
        result.Rejection.Field.Should().Be(field, because);
    }

    [Theory]
    [MemberData(nameof(BadBodies))]
    public void Parse_BusBody_BadAlphabet_LongLine_WrongLength_WrongDigest_InvalidFieldBody(string because, byte[] file)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);

        // Assert
        result.IsAccepted.Should().BeFalse(because);
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField, because);
        result.Rejection.Field.Should().Be("body", because);
    }

    [Fact]
    public void Parse_OneDriveItemWithBody_InvalidFieldBody()
    {
        // Arrange
        byte[] file = OneDrive.WithBody(ItemBody.Encode(TextItem)).ToBytes();

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be("body");
    }

    [Fact]
    public void Parse_BusBodyLongerThanCeilingBound_InvalidFieldBody_BeforeDecoding()
    {
        // Arrange: one character over the bound, and the last one is not base64 — the length is judged first (AC-4)
        int length = checked((int)ItemBody.MaxBodyLength(ArchiveOptions.ItemCeiling) + 1);
        byte[] body = new byte[length];
        body.AsSpan().Fill((byte)'A');
        body[^1] = (byte)'*';
        byte[] file = Bus.WithBody(body).ToBytes();

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be("body");
        result.Rejection.Detail.Should().Contain("longer than").And.NotContain("base64 characters");
    }

    [Fact]
    public async Task Verify_ForeignTenant_TenantMismatch_StoreNeverAsked()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);
        byte[] file = Bus.WithValue("tenant", "globex").WithValue("key_id", "globex/1").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.TenantMismatch);
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Verify_KeyIdOfAnotherTenant_KeyIdTenantMismatch_StoreNeverAsked()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Bus.WithValue("key_id", "globex/1").ToBytes(), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.KeyIdTenantMismatch);
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    internal static EnvelopeHeader Header() => new(
        Acme,
        EnvelopeId.Parse("01JBXZ4K7Q2X9Z4A5B6C7D8E9F"),
        MachineName.Parse("home-laptop"),
        MachineName.Central,
        new DateTimeOffset(2026, 10, 31, 14, 5, 0, TimeSpan.Zero));

    internal static ItemFields Fields(ItemRoute via, string? driveName, byte[]? bytes = null)
    {
        byte[] file = bytes ?? TextItem;
        return new ItemFields(
            Slug.Parse("calizr"),
            Slug.Parse("staging-restore-notes"),
            "Staging restore notes",
            "How the staging database was restored",
            "restore-notes.md",
            file.Length,
            Convert.ToHexStringLower(SHA256.HashData(file)),
            via,
            driveName);
    }

    private static async Task<Model> RoundTripAsync(EnvelopeSigner signer, Model unsigned)
    {
        EnvelopeResult signed = await signer.SignAsync(unsigned, TestKeys.Acme1, Ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        EnvelopeResult reparsed = await signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed.Envelope!), Acme, Ct);
        reparsed.IsAccepted.Should().BeTrue(reparsed.Rejection?.ToString());
        return reparsed.Envelope!;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
