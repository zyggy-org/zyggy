using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary><c>instance/linkedin.json</c> (spec 36 Configuration): the keys, their defaults and the first error naming the key.</summary>
public sealed class LinkedInConfigurationTests : IDisposable
{
    private readonly LinkedInFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Load_FixtureFile_ValuesAndDefaults()
    {
        // Act
        var load = LinkedInConfiguration.Load(_fixture.Environment);

        // Assert
        load.Error.Should().BeNull();
        var config = load.Configuration!;
        config.ClientId.Should().Be("clientid0001");
        config.RedirectUri.Should().Be("https://localhost/zyggy/linkedin");
        config.MemberSub.Should().BeNull();
        config.ApiVersion.Should().Be("202609");
        config.ActionsEnabled.Should().Equal("post");
        config.PostEnabled.Should().BeTrue();
        config.PostMaxChars.Should().Be(3000);
        config.ExpiryWarnDays.Should().Be(7);
    }

    [Fact]
    public void Load_EveryKeySet_Values()
    {
        // Arrange
        _fixture.WriteInstance("""
            {"client_id":"abc123","redirect_uri":"http://localhost:8123/cb","member_sub":"sub_A-1","api_version":"202610",
             "actions":{"enabled":["post"]},"post":{"max_chars":2900},"expiry_warn_days":14}
            """);

        // Act
        var config = LinkedInConfiguration.Load(_fixture.Environment).Configuration!;

        // Assert
        config.MemberSub.Should().Be("sub_A-1");
        config.ApiVersion.Should().Be("202610");
        config.PostMaxChars.Should().Be(2900);
        config.ExpiryWarnDays.Should().Be(14);
        config.RedirectUri.Should().Be("http://localhost:8123/cb");
    }

    [Fact]
    public void Load_ActionsEnabledEmpty_Accepted()
    {
        // Arrange
        _fixture.WriteInstance("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"enabled":[]}}""");

        // Act
        var load = LinkedInConfiguration.Load(_fixture.Environment);

        // Assert
        load.Error.Should().BeNull();
        load.Configuration!.PostEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "is missing")]
    [InlineData("{not json", "not valid JSON")]
    [InlineData("""{"redirect_uri":"https://localhost/cb"}""", "client_id is required")]
    [InlineData("""{"client_id":"client-id-0001","redirect_uri":"https://localhost/cb"}""", "client_id must be")]
    [InlineData("""{"client_id":"abc"}""", "redirect_uri is required")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb?x=1"}""", "redirect_uri must be")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb#f"}""", "redirect_uri must be")]
    [InlineData("""{"client_id":"abc","redirect_uri":"http://example.com/cb"}""", "redirect_uri must be")]
    [InlineData("""{"client_id":"abc","redirect_uri":"/relative"}""", "redirect_uri must be")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","member_sub":"a b"}""", "member_sub must be")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","api_version":"2026-09"}""", "api_version must be YYYYMM")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"enabled":["comment"]}}""", "actions.enabled may only list")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"enabled":["post","post"]}}""", "actions.enabled may only list")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"other":1}}""", "actions.other is not a known key")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","post":{"max_chars":0}}""", "post.max_chars must be an integer 1..3000")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","post":{"max_chars":3001}}""", "post.max_chars must be an integer 1..3000")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","post":{"max_chars":12.5}}""", "post.max_chars must be an integer 1..3000")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","expiry_warn_days":0}""", "expiry_warn_days must be an integer 1..30")]
    [InlineData("""{"client_id":"abc","redirect_uri":"https://localhost/cb","scopes":"r_member_social"}""", "scopes is not a known key")]
    public void Load_Misconfigured_ExitThreeNamesKey(string? json, string expected)
    {
        // Arrange
        if (json is null)
        {
            File.Delete(_fixture.InstanceFile);
        }
        else
        {
            _fixture.WriteInstance(json);
        }

        // Act
        var load = LinkedInConfiguration.Load(_fixture.Environment);

        // Assert
        load.Configuration.Should().BeNull();
        load.Error.Should().StartWith("configuration error: " + _fixture.InstanceFile).And.Contain(expected);
    }

    [Fact]
    public void Load_NoInstanceVariable_ConfigurationError()
    {
        // Arrange
        _fixture.Environment.Remove("ZYGGY_INSTANCE_DIR");

        // Act
        var load = LinkedInConfiguration.Load(_fixture.Environment);

        // Assert
        load.Error.Should().Be("configuration error: ZYGGY_INSTANCE_DIR is not set (and no CLAUDE_PROJECT_DIR to derive it from)");
    }

    [Fact]
    public void Load_ClaudeProjectDir_InstanceUnderIt()
    {
        // Arrange
        _fixture.Environment.Remove("ZYGGY_INSTANCE_DIR");
        _fixture.Environment["CLAUDE_PROJECT_DIR"] = _fixture.Root;

        // Act
        var load = LinkedInConfiguration.Load(_fixture.Environment);

        // Assert
        load.Configuration!.Path.Should().Be(Path.Join(_fixture.Root, "instance", "linkedin.json"));
    }
}
