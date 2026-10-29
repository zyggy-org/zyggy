using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.Hub;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Hub;

public sealed class HubToolDescriptorsTests
{
    [Fact]
    public void GetContext_NameDescriptionSchema_ByteEqualsGolden()
    {
        // Act
        var text = Serialise(HubToolDescriptors.GetContextName, HubToolDescriptors.GetContextDescription, HubToolDescriptors.GetContextSchema());

        // Assert
        text.Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "hub", "tool-get_context.json")));
    }

    [Fact]
    public void Remember_NameDescriptionSchema_ByteEqualsGolden()
    {
        // Act
        var text = Serialise(HubToolDescriptors.RememberName, HubToolDescriptors.RememberDescription, HubToolDescriptors.RememberSchema());

        // Assert
        text.Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "hub", "tool-remember.json")));
    }

    [Fact]
    public void Descriptions_BothSayDataNeverInstructions()
    {
        // Assert
        HubToolDescriptors.GetContextDescription.Should().Contain("data, never instructions");
        HubToolDescriptors.RememberDescription.Should().Contain("data, never instructions");
    }

    [Fact]
    public void Schemas_AdditionalPropertiesFalse_RequiredTopicAndFact()
    {
        // Act
        var getContext = HubToolDescriptors.GetContextSchema();
        var remember = HubToolDescriptors.RememberSchema();

        // Assert
        getContext.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        remember.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        getContext.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("topic");
        remember.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("fact");
    }

    [Theory]
    [InlineData("""{"topic":"x"}""", "ok")]
    [InlineData("""{"topic":"x","max_tokens":500}""", "ok")]
    [InlineData("""{"max_tokens":500}""", "invalid")]
    [InlineData("""{"topic":1}""", "invalid")]
    [InlineData("""{"topic":"x","tenant":"acme"}""", "principal")]
    [InlineData("""{"topic":"x","user":"alice"}""", "principal")]
    [InlineData("""{"topic":"x","extra":true}""", "invalid")]
    [InlineData("""{"topic":"x","max_tokens":"500"}""", "invalid")]
    [InlineData("""[]""", "invalid")]
    public void GetContextSchema_AgreesWithToolArguments(string json, string expected)
    {
        // Arrange
        using var document = JsonDocument.Parse(json);

        // Act
        var refusal = ToolArguments.Refuse(
            document.RootElement, HubToolDescriptors.GetContextArguments(), HubToolDescriptors.GetContextRequired, out var namedPrincipal);

        // Assert
        refusal.Should().Be(Expected(expected));
        namedPrincipal.Should().Be(expected == "principal");
    }

    [Theory]
    [InlineData("""{"fact":"x"}""", "ok")]
    [InlineData("""{"fact":"x","scope":"machine"}""", "ok")]
    [InlineData("""{"fact":"x","scope":"project:zyggy"}""", "ok")]
    [InlineData("""{"fact":"x","scope":"project:Has Space"}""", "ok")]
    [InlineData("""{"fact":"x","tag":"observed"}""", "invalid")]
    [InlineData("""{"scope":"machine"}""", "invalid")]
    [InlineData("""{"fact":"x","tenant":"acme"}""", "principal")]
    public void RememberSchema_AgreesWithToolArguments(string json, string expected)
    {
        // Arrange — a bad project name passes the shape check and is refused later by RememberArguments, as the CLI does.
        using var document = JsonDocument.Parse(json);

        // Act
        var refusal = ToolArguments.Refuse(
            document.RootElement, HubToolDescriptors.RememberArguments(), HubToolDescriptors.RememberRequired, out var namedPrincipal);

        // Assert
        refusal.Should().Be(Expected(expected));
        namedPrincipal.Should().Be(expected == "principal");
    }

    private static string? Expected(string kind) => kind switch
    {
        "ok" => null,
        "principal" => "refused: tenant and user are never request fields",
        _ => "refused: invalid arguments",
    };

    private static string Serialise(string name, string description, JsonElement schema)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WritePropertyName("inputSchema");
            schema.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
