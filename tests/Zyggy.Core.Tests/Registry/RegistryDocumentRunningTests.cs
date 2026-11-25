using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Spec 61 slice 6 (AC-10, document half): the optional registry key <c>running</c>.</summary>
public sealed class RegistryDocumentRunningTests
{
    private static readonly EnvelopeId First = EnvelopeId.Parse("01M87GK2G7EARZCD9D1ZB75GY8");

    private static readonly EnvelopeId Second = EnvelopeId.Parse("01M87G38KYNXKBF5FCA14GHSGX");

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // AC-10: the ids are written as a list in ordinal order under the key running.
    [Fact]
    public void Serialize_RunningIds_ListedInOrdinalOrder()
    {
        // Arrange
        RegistryDocument document = RegistryDocumentTests.HomeLaptop() with { Running = [First, Second] };

        // Act
        string text = Text(document.Serialize());

        // Assert
        text.Should().Contain("\nrunning: [" + Second.Value + ", " + First.Value + "]\n");
    }

    // AC-10: a document with running parses back equal.
    [Fact]
    public void Parse_SerializedWithRunning_RoundTripsEqual()
    {
        // Arrange
        RegistryDocument document = RegistryDocumentTests.HomeLaptop() with { Running = [Second, First] };

        // Act
        RegistryParse parse = RegistryDocument.Parse(document.Serialize());

        // Assert
        parse.Document.Should().NotBeNull();
        parse.Document!.Running.Should().Equal(Second, First);
        parse.Document.Should().Be(document);
    }

    // AC-10: nothing running omits the key, so the file is byte for byte what 0.30.0 wrote (the goldens are unchanged).
    [Fact]
    public void Serialize_NothingRunning_KeyOmittedGoldenUnchanged()
    {
        // Act
        string text = Text(RegistryDocumentTests.HomeLaptop().Serialize());

        // Assert
        text.Should().NotContain("running");
    }

    // AC-10: a file without the key (every 0.30.0 file) parses as today, with nothing running.
    [Fact]
    public void Parse_FileWithoutTheKey_NothingRunning()
    {
        // Act
        RegistryParse parse = RegistryDocument.Parse(RegistryDocumentTests.HomeLaptop().Serialize());

        // Assert
        parse.Document!.Running.Should().BeEmpty();
    }

    // AC-10: a running entry that is not a job id refuses the file, naming the key.
    [Fact]
    public void Parse_RunningEntryNotAnId_RefusedNamingRunning()
    {
        // Arrange
        string text = Text(RegistryDocumentTests.HomeLaptop().Serialize()) + "running: [not-an-id]\n";

        // Act
        RegistryParse parse = RegistryDocument.Parse(Encoding.UTF8.GetBytes(text));

        // Assert
        parse.Field.Should().Be("running");
    }

    // A changed running list is content: two documents that differ only there are not equal.
    [Fact]
    public void ContentEquals_RunningDiffers_False()
    {
        // Arrange
        RegistryDocument document = RegistryDocumentTests.HomeLaptop();

        // Act
        bool equal = document.ContentEquals(document with { Running = [First] });

        // Assert
        equal.Should().BeFalse();
    }
}
