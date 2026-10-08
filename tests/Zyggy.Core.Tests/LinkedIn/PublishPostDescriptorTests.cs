using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The one tool's name, description and strict schema (spec 36 AC-1), and that the handler's own parsing agrees with it.</summary>
public sealed class PublishPostDescriptorTests
{
    [Fact]
    public void Schema_3000_ByteEqualsGolden()
    {
        // Act
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", PublishPostDescriptor.Name);
            writer.WriteString("description", PublishPostDescriptor.Description);
            writer.WritePropertyName("inputSchema");
            PublishPostDescriptor.InputSchema(3000).WriteTo(writer);
            writer.WriteEndObject();
        }

        // Assert
        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "linkedin", "tool-publish_post.json")));
    }

    [Fact]
    public void Schema_LoweredMaxChars_MaxLengthFollows()
    {
        // Act
        var schema = PublishPostDescriptor.InputSchema(1200);

        // Assert
        schema.GetProperty("properties").GetProperty("text").GetProperty("maxLength").GetInt32().Should().Be(1200);
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"text":"Hello","visibility":"PUBLIC"}""", true)]
    [InlineData("""{"visibility":"CONNECTIONS","text":"Hello"}""", true)]
    [InlineData("""{"text":"Hello"}""", false)]
    [InlineData("""{"visibility":"PUBLIC"}""", false)]
    [InlineData("""{"text":"Hello","visibility":"PUBLIC","x":1}""", false)]
    [InlineData("""{"text":1,"visibility":"PUBLIC"}""", false)]
    [InlineData("""{"text":"Hello","visibility":"public"}""", false)]
    [InlineData("""{"text":"Hello","visibility":["PUBLIC"]}""", false)]
    [InlineData("""[]""", false)]
    public void Schema_AgreesWithPostArguments(string json, bool schemaValid)
    {
        // Arrange: the sample's validity under the schema, judged by hand from its keywords
        using var sample = JsonDocument.Parse(json);

        // Act
        var parsed = PostArguments.TryParse(sample.RootElement, out _, out _);

        // Assert
        parsed.Should().Be(schemaValid);
    }

    [Fact]
    public void Schema_LengthBounds_EnforcedByThePolicy()
    {
        // The schema's minLength 1 and maxLength max_chars are re-checked by the handler's policy, not trusted from the client.
        PostPolicy.Check(string.Empty, 3000, PublishHarness.Patterns).Should().Be("empty");
        PostPolicy.Check(new string('a', 3001), 3000, PublishHarness.Patterns).Should().Be("too long (3001 > 3000)");
        PostPolicy.Check(string.Concat(Enumerable.Repeat("ab ", 1000)), 3000, PublishHarness.Patterns).Should().BeNull();
    }
}
