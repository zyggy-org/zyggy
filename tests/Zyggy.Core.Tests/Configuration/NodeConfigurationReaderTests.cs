using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>Spec 04 AC-30: the minimal node.json, its refusals and the path precedence.</summary>
public sealed class NodeConfigurationReaderTests : IDisposable
{
    private const string Minimal = """
        {
          "tenant": "acme",
          "machine": "home-laptop",
          "signWith": "acme/1",
          "bus": {
            "owner": "acme-org",
            "repo": "acme-bus",
            "branch": "main",
            "localPath": "{root}",
            "remote": "https://example.invalid/acme-org/acme-bus.git"
          },
          "poll": { "activeSeconds": 10, "idleSeconds": 60 },
          "secrets": { "root": null }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    public NodeConfigurationReaderTests() => Directory.CreateDirectory(_dir);

    /// <summary>The scratch directory as written into the JSON (forward slashes).</summary>
    private string Root => _dir.Replace('\\', '/');

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_MinimalSpecFile_TypedConfiguration()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        NodeConfiguration cfg = result.Configuration!;
        cfg.Tenant.Should().Be(TenantId.Parse("acme"));
        cfg.Machine.Should().Be(MachineName.Parse("home-laptop"));
        cfg.SignWith.Should().Be(KeyId.Parse("acme/1"));
        cfg.Bus.Should().Be(new BusSettings("acme-org", "acme-bus", "main", Root, "https://example.invalid/acme-org/acme-bus.git"));
        cfg.Poll.Should().Be(new PollSettings(10, 60));
        cfg.Secrets.Root.Should().BeNull();
    }

    [Fact]
    public void Read_UnknownKeys_Ignored()
    {
        // Arrange: the Node service's keys
        string json = Minimal.Replace("\"tenant\": \"acme\",", "\"tenant\": \"acme\", \"role\": \"node\", \"workHours\": { \"from\": \"08:00\" }, \"claude\": { \"path\": \"claude\" },", StringComparison.Ordinal);

        // Act
        NodeConfigurationResult result = Read(json);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
    }

    [Fact]
    public void Read_MissingFile_FileMissingWithPath()
    {
        // Act
        NodeConfigurationResult result = NodeConfigurationReader.Read(Path.Combine(_dir, "absent.json"));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.FileMissing);
        result.Detail.Should().Contain("absent.json");
    }

    [Fact]
    public void Read_InvalidJson_Malformed()
    {
        // Act
        NodeConfigurationResult result = Read("{ not json");

        // Assert
        result.Error.Should().Be(NodeConfigurationError.Malformed);
    }

    [Fact]
    public void Read_TenantMissing_MissingFieldTenant()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"tenant\": \"acme\",", string.Empty, StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.MissingField);
        result.Field.Should().Be("tenant");
        result.ToString().Should().Be("tenant: tenant missing");
    }

    [Fact]
    public void Read_TenantUppercase_InvalidFieldTenant()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"tenant\": \"acme\"", "\"tenant\": \"Acme\"", StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("tenant");
    }

    [Fact]
    public void Read_SignWithOtherTenant_InvalidSignWithTenantDiffers()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"signWith\": \"acme/1\"", "\"signWith\": \"globex/1\"", StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("signWith");
        result.Detail.Should().Be("tenant differs");
    }

    [Fact]
    public void Read_SignWithAbsent_NullSignWith()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"signWith\": \"acme/1\",", string.Empty, StringComparison.Ordinal));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.SignWith.Should().BeNull();
    }

    [Fact]
    public void Read_BranchAbsent_DefaultsMain()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"branch\": \"main\",", string.Empty, StringComparison.Ordinal));

        // Assert
        result.Configuration!.Bus.Branch.Should().Be("main");
    }

    [Fact]
    public void Read_PollAbsent_DefaultsTenSixty()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"poll\": { \"activeSeconds\": 10, \"idleSeconds\": 60 },", string.Empty, StringComparison.Ordinal));

        // Assert
        result.Configuration!.Poll.Should().Be(new PollSettings(10, 60));
        result.Configuration.Poll.ToPollOptions().ActiveInterval.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Read_PollZero_InvalidField()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"activeSeconds\": 10", "\"activeSeconds\": 0", StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("poll.activeSeconds");
    }

    [Fact]
    public void Read_RelativeLocalPath_InvalidField()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("{root}", "relative/bus", StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("bus.localPath");
    }

    [Theory]
    [InlineData("\"pat\": \"x\",", "pat")]
    [InlineData("\"token\": \"x\",", "token")]
    [InlineData("\"hmac\": \"x\",", "hmac")]
    [InlineData("\"secret\": \"x\",", "secret")]
    [InlineData("\"password\": \"x\",", "password")]
    [InlineData("\"Secrets\": { \"root\": null },", "Secrets")]
    public void Read_SecretLookingKeyTopLevel_Refused(string insert, string key)
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"tenant\": \"acme\",", "\"tenant\": \"acme\", " + insert, StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be(key);
        result.Detail.Should().Be("secrets never in node.json");
    }

    [Fact]
    public void Read_SecretLookingKeyNested_RefusedWithDottedPath()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal.Replace("\"owner\": \"acme-org\",", "\"owner\": \"acme-org\", \"GitHubToken\": \"x\",", StringComparison.Ordinal));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("bus.GitHubToken");
    }

    [Fact]
    public void Read_SecretsRootSection_Allowed()
    {
        // Act: the documented section names a location, not a secret
        NodeConfigurationResult result = Read(Minimal.Replace("\"secrets\": { \"root\": null }", "\"secrets\": { \"root\": \"{root}\" }", StringComparison.Ordinal));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Secrets.Root.Should().Be(Root);
    }

    [Fact]
    public void ResolvePath_Precedence_OptionThenEnvThenDefault()
    {
        // Arrange
        var env = new Dictionary<string, string?> { ["ZYGGY_NODE_JSON"] = "/from/env/node.json", ["LOCALAPPDATA"] = "/local", ["HOME"] = "/home/u", ["XDG_CONFIG_HOME"] = "/xdg" };

        // Act
        string fromOption = NodeConfigurationReader.ResolvePath("/opt/node.json", env);
        string fromEnv = NodeConfigurationReader.ResolvePath(null, env);
        string fromDefault = NodeConfigurationReader.ResolvePath(null, new Dictionary<string, string?> { ["LOCALAPPDATA"] = "/local", ["HOME"] = "/home/u", ["XDG_CONFIG_HOME"] = "/xdg" });

        // Assert
        fromOption.Should().Be("/opt/node.json");
        fromEnv.Should().Be("/from/env/node.json");
        fromDefault.Should().Be(OperatingSystem.IsWindows() ? Path.Join("/local", "zyggy", "node.json") : Path.Join("/xdg", "zyggy", "node.json"));
    }

    [Fact]
    public void ResolvePath_Default_PerOs()
    {
        // Act: spec 04 Assumption A5 — ~/.config/zyggy/node.json on Linux (Central's directory), %LOCALAPPDATA%\zyggy\node.json on Windows
        string path = NodeConfigurationReader.ResolvePath(null, new Dictionary<string, string?> { ["LOCALAPPDATA"] = "/local", ["HOME"] = "/home/u" });

        // Assert
        path.Should().Be(OperatingSystem.IsWindows() ? Path.Join("/local", "zyggy", "node.json") : Path.Join("/home/u", ".config", "zyggy", "node.json"));
    }

    private const string ClaudeSection = """
        "claude": { "path": "/opt/claude/claude", "maxTurns": 40, "defaultTimeoutMinutes": 15, "defaultAllowedTools": ["Read", "Edit"], "maxBudgetUsd": 5 },
        """;

    [Fact]
    public void Read_ClaudeSection_Typed()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel(ClaudeSection));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        ClaudeSettings claude = result.Configuration!.Claude;
        claude.Path.Should().Be("/opt/claude/claude");
        claude.MaxTurns.Should().Be(40);
        claude.DefaultTimeoutMinutes.Should().Be(15);
        claude.DefaultAllowedTools.Should().Equal("Read", "Edit");
        claude.EffectiveAllowedTools.Should().Equal("Read", "Edit");
        claude.MaxBudgetUsd.Should().Be(5m);
    }

    [Fact]
    public void Read_ClaudeAbsent_Defaults()
    {
        // Act
        NodeConfigurationResult result = Read(Minimal);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        NodeConfiguration cfg = result.Configuration!;
        cfg.Claude.MaxTurns.Should().Be(60);
        cfg.Claude.DefaultTimeoutMinutes.Should().Be(30);
        cfg.Claude.EffectiveAllowedTools.Should().Equal("Read", "Grep", "Glob");
        cfg.Claude.MaxBudgetUsd.Should().BeNull();
        cfg.Claude.Path.Should().BeNull();
        cfg.DevRoots.Should().BeEmpty();
        cfg.NodeDir.Should().BeNull();
    }

    [Theory]
    [InlineData("\"claude\": { \"maxTurns\": 0 },", "claude.maxTurns")]
    [InlineData("\"claude\": { \"defaultTimeoutMinutes\": 0 },", "claude.defaultTimeoutMinutes")]
    [InlineData("\"claude\": { \"maxBudgetUsd\": -1 },", "claude.maxBudgetUsd")]
    [InlineData("\"claude\": { \"maxBudgetUsd\": \"5\" },", "claude.maxBudgetUsd")]
    [InlineData("\"claude\": { \"defaultAllowedTools\": \"Read\" },", "claude.defaultAllowedTools")]
    [InlineData("\"claude\": { \"defaultAllowedTools\": [\"Read\", \"\"] },", "claude.defaultAllowedTools")]
    [InlineData("\"claude\": { \"path\": \"\" },", "claude.path")]
    [InlineData("\"claude\": \"x\",", "claude")]
    [InlineData("\"devRoots\": \"/src\",", "devRoots")]
    public void Read_ClaudeInvalid_InvalidFieldNamesKey(string insert, string field)
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel(insert));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be(field);
    }

    [Fact]
    public void Read_MaxBudgetUsdNull_Null()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel("\"claude\": { \"maxBudgetUsd\": null },"));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Claude.MaxBudgetUsd.Should().BeNull();
    }

    [Fact]
    public void Read_DevRoots_Typed()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel("\"devRoots\": [\"{root}/src\", \"{root}/work\"],"));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.DevRoots.Should().Equal(Root + "/src", Root + "/work");
    }

    [Fact]
    public void Read_DevRootsRelativeEntry_InvalidField()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel("\"devRoots\": [\"{root}/src\", \"relative/work\"],"));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("devRoots[1]");
        result.Detail.Should().Be("must be an absolute path");
    }

    [Fact]
    public void Read_NodeDir_Typed()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel("\"nodeDir\": \"{root}/node\","));

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.NodeDir.Should().Be(Root + "/node");
    }

    [Fact]
    public void Read_NodeDirRelative_InvalidField()
    {
        // Act
        NodeConfigurationResult result = Read(WithTopLevel("\"nodeDir\": \"node\","));

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be("nodeDir");
    }

    private static string WithTopLevel(string insert) =>
        Minimal.Replace("\"tenant\": \"acme\",", "\"tenant\": \"acme\", " + insert, StringComparison.Ordinal);

    private NodeConfigurationResult Read(string json)
    {
        string path = Path.Combine(_dir, "node.json");
        File.WriteAllText(path, json.Replace("{root}", _dir.Replace('\\', '/'), StringComparison.Ordinal));
        return NodeConfigurationReader.Read(path);
    }
}
