using System.Text.Json;

using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 Contracts: the mail run's schema is embedded, draft-07, with the closed enums and length caps.</summary>
public sealed class BriefSchemaTests
{
    [Fact]
    public void Schema_IsDraft07AndEmbedded()
    {
        // Act
        using var schema = JsonDocument.Parse(new BriefPrompts().MailSchema);

        // Assert
        schema.RootElement.GetProperty("$schema").GetString().Should().Be("http://json-schema.org/draft-07/schema#");
        schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("mail", "files", "replies", "facts");
    }

    [Fact]
    public void Schema_ClassAndActionEnumsAndLengthCaps()
    {
        // Act
        using var schema = JsonDocument.Parse(new BriefPrompts().MailSchema);
        var mail = schema.RootElement.GetProperty("properties").GetProperty("mail").GetProperty("items").GetProperty("properties");

        // Assert
        mail.GetProperty("class").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("urgent", "important", "other");
        mail.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("z", "you", "nothing");
        mail.GetProperty("summary").GetProperty("maxLength").GetInt32().Should().Be(200);
        mail.GetProperty("z").GetProperty("properties").GetProperty("why").GetProperty("maxLength").GetInt32().Should().Be(120);
        mail.GetProperty("you").GetProperty("properties").GetProperty("action").GetProperty("maxLength").GetInt32().Should().Be(80);
        mail.GetProperty("amount").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("read", "stated", "not_read");
        schema.RootElement.GetProperty("properties").GetProperty("files").GetProperty("items").GetProperty("properties").GetProperty("tiedTo").GetProperty("type").GetString().Should().Be("string");
    }
}
