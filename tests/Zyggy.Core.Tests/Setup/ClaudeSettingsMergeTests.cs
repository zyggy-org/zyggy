using System.Text;
using System.Text.Json.Nodes;

using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>
/// Spec 42 AC-13..AC-15, the Configuration table and Edge Cases (plan 42 A7): the user-scope <c>settings.json</c> created or merged additively —
/// backup first, existing keys, values and order kept, deny unioned, the owner's other keys never touched, invalid JSON left untouched.
/// </summary>
public sealed class ClaudeSettingsMergeTests : IDisposable
{
    private const string OwnerSettings = """
        {
          "model": "opus",
          "env": { "OTHER": "1" },
          "permissions": {
            "allow": [ "Bash(git status)", "Read(*)" ],
            "deny": [ "Bash(zyggy secret*)", "Bash(rm -rf*)" ]
          },
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "zyggy context --stop-hook", "timeout": 30 } ] } ]
          }
        }
        """;

    private readonly SetupTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    private string ClaudeDir => Path.Combine(_files.Root, "claude");

    private string SettingsFile => SetupPaths.SettingsFile(ClaudeDir);

    private string BackupFile => SetupPaths.Backup(SettingsFile, SetupTestFiles.Stamp);

    private string CorePath => SetupTestFiles.Slashes(Path.Combine(_files.Root, "core"));

    private string Patterns => SetupPaths.SecretPatterns(CorePath);

    [Fact]
    public void Apply_FileAbsent_CreatedWithExactlyEnvAndThreeDenyRules_TwoDoneLines_NoBackup()
    {
        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        string expected = "{\n  \"env\": {\n    \"ZYGGY_SECRET_PATTERNS\": \"" + Patterns + "\"\n  },\n  \"permissions\": {\n    \"deny\": [\n"
            + "      \"Bash(zyggy secret*)\",\n      \"Bash(zyggy context --stop-hook*)\",\n      \"Bash(zyggy context --flush*)\"\n    ]\n  }\n}\n";
        File.ReadAllText(SettingsFile).Should().Be(expected);
        File.ReadAllBytes(SettingsFile).AsSpan().StartsWith(Encoding.UTF8.Preamble).Should().BeFalse();
        lines.Should().Equal(
            new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Done, "env.ZYGGY_SECRET_PATTERNS set to " + Patterns + " (created " + SettingsFile + ")"),
            new SetupLine(SetupItem.SettingsDeny, SetupOutcome.Done, "permissions.deny gained " + string.Join(", ", ClaudeSettingsMerge.DenyRules) + " (created " + SettingsFile + ")"));
        Directory.GetFiles(ClaudeDir).Should().Equal(SettingsFile);
    }

    [Fact]
    public void Apply_FilePresent_BackupWritten_ExistingKeysValuesAndOrderKept_EnvAdded_DenyUnioned()
    {
        // Arrange
        Seed(OwnerSettings);
        JsonObject before = JsonNode.Parse(OwnerSettings)!.AsObject();

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        File.ReadAllText(BackupFile).Should().Be(OwnerSettings);
        JsonObject after = JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
        after.Select(p => p.Key).Should().Equal("model", "env", "permissions", "hooks");
        after["model"]!.GetValue<string>().Should().Be("opus");
        after["env"]!.AsObject().Select(p => p.Key).Should().Equal("OTHER", "ZYGGY_SECRET_PATTERNS");
        after["env"]!["OTHER"]!.GetValue<string>().Should().Be("1");
        after["env"]!["ZYGGY_SECRET_PATTERNS"]!.GetValue<string>().Should().Be(Patterns);
        JsonNode.DeepEquals(after["permissions"]!["allow"], before["permissions"]!["allow"]).Should().BeTrue();
        after["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(
            "Bash(zyggy secret*)", "Bash(rm -rf*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)");
        after["hooks"]!.ToJsonString().Should().Be(before["hooks"]!.ToJsonString());
        lines.Should().Equal(
            new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Done, "env.ZYGGY_SECRET_PATTERNS set to " + Patterns + " (backup " + BackupFile + ")"),
            new SetupLine(SetupItem.SettingsDeny, SetupOutcome.Done, "permissions.deny gained Bash(zyggy context --stop-hook*), Bash(zyggy context --flush*) (backup " + BackupFile + ")"));
    }

    [Theory]
    [InlineData("{ \"model\": \"opus\" }")]
    [InlineData(OwnerSettings)]
    public void Apply_HooksNeverAddedChangedOrRemoved(string settings)
    {
        // Arrange
        Seed(settings);
        string? before = JsonNode.Parse(settings)!["hooks"]?.ToJsonString();

        // Act
        Merge();

        // Assert
        (JsonNode.Parse(File.ReadAllText(SettingsFile))!["hooks"]?.ToJsonString()).Should().Be(before);
    }

    [Fact]
    public void Apply_HooksAbsent_StillAbsentAfterMerge()
    {
        // Arrange
        Seed("{ \"model\": \"opus\" }");

        // Act
        Merge();

        // Assert
        JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject().Select(p => p.Key).Should().Equal("model", "env", "permissions");
    }

    [Fact]
    public void Apply_EnvAlreadyOurs_DenyAlreadyPresent_AlreadyDone_NoWriteNoBackup_LastWriteTimeUnchanged()
    {
        // Arrange
        Merge();
        DateTime written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SettingsFile, written);
        byte[] bytes = File.ReadAllBytes(SettingsFile);

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines.Should().Equal(
            new SetupLine(SetupItem.SettingsEnv, SetupOutcome.AlreadyDone, "env.ZYGGY_SECRET_PATTERNS is " + Patterns),
            new SetupLine(SetupItem.SettingsDeny, SetupOutcome.AlreadyDone, "permissions.deny holds the three zyggy rules"));
        File.ReadAllBytes(SettingsFile).Should().Equal(bytes);
        File.GetLastWriteTimeUtc(SettingsFile).Should().Be(written);
        Directory.GetFiles(ClaudeDir).Should().Equal(SettingsFile);
    }

    [Fact]
    public void Apply_EnvOtherValueWhoseFileExists_LeftAsIs_ExitZeroOutcome()
    {
        // Arrange
        string other = SetupTestFiles.Slashes(Path.Combine(_files.Root, "patterns.txt"));
        File.WriteAllText(other, "synthetic-pattern\n");
        Seed("{ \"env\": { \"ZYGGY_SECRET_PATTERNS\": \"" + other + "\" } }");

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines[0].Should().Be(new SetupLine(SetupItem.SettingsEnv, SetupOutcome.LeftAsIs, "ZYGGY_SECRET_PATTERNS is " + other));
        JsonNode.Parse(File.ReadAllText(SettingsFile))!["env"]!["ZYGGY_SECRET_PATTERNS"]!.GetValue<string>().Should().Be(other);
        SetupExit.Worst(lines).Should().Be(SetupExit.Ok);
    }

    [Fact]
    public void Apply_EnvOtherValueWhoseFileIsMissing_Failed()
    {
        // Arrange
        string other = SetupTestFiles.Slashes(Path.Combine(_files.Root, "gone.txt"));
        Seed("{ \"env\": { \"ZYGGY_SECRET_PATTERNS\": \"" + other + "\" } }");

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines[0].Should().Be(new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Failed, "ZYGGY_SECRET_PATTERNS is " + other + ", which does not exist; left as is"));
        JsonNode.Parse(File.ReadAllText(SettingsFile))!["env"]!["ZYGGY_SECRET_PATTERNS"]!.GetValue<string>().Should().Be(other);
        SetupExit.Worst(lines).Should().Be(SetupExit.Incomplete);
    }

    [Theory]
    [InlineData("{ \"model\": ")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{ \"env\": \"not an object\" }")]
    [InlineData("{ \"permissions\": { \"deny\": \"Bash(x)\" } }")]
    [InlineData("{ \"permissions\": [] }")]
    public void Apply_NotValidJson_OneFailedLine_FileUntouchedNoBackup(string settings)
    {
        // Arrange
        Seed(settings);
        byte[] original = File.ReadAllBytes(SettingsFile);

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines.Should().Equal(new SetupLine(SetupItem.SettingsEnv, SetupOutcome.Failed, "not valid JSON; left untouched"));
        File.ReadAllBytes(SettingsFile).Should().Equal(original);
        Directory.GetFiles(ClaudeDir).Should().Equal(SettingsFile);
    }

    [Fact]
    public void Apply_RootNotAnObject_Failed()
    {
        // Arrange
        Seed("\"just a string\"");

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines.Should().ContainSingle().Which.Outcome.Should().Be(SetupOutcome.Failed);
    }

    [Fact]
    public void Apply_NotWritable_FailedWithWriteError_BackupNotLeftBehind()
    {
        // Arrange
        Seed("{ \"model\": \"opus\" }");
        byte[] original = File.ReadAllBytes(SettingsFile);
        ReadOnly(SettingsFile, readOnly: true);
        try
        {
            // Act
            IReadOnlyList<SetupLine> lines = Merge();

            // Assert
            SetupLine line = lines.Should().ContainSingle().Subject;
            line.Item.Should().Be(SetupItem.SettingsEnv);
            line.Outcome.Should().Be(SetupOutcome.Failed);
            line.Detail.Should().StartWith(SettingsFile + " could not be written: ").And.EndWith("; left untouched");
            File.ReadAllBytes(SettingsFile).Should().Equal(original);
            Directory.GetFiles(ClaudeDir).Should().Equal(SettingsFile);
        }
        finally
        {
            ReadOnly(SettingsFile, readOnly: false);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public void Apply_EmptyFile_TreatedAsEmptyObject_WithBackup(string settings)
    {
        // Arrange
        Seed(settings);

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines.Select(l => l.Outcome).Should().Equal(SetupOutcome.Done, SetupOutcome.Done);
        File.ReadAllText(BackupFile).Should().Be(settings);
        JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject().Select(p => p.Key).Should().Equal("env", "permissions");
    }

    [Fact]
    public void Apply_Utf8Bom_ParsedAndWrittenWithoutBom()
    {
        // Arrange
        Directory.CreateDirectory(ClaudeDir);
        File.WriteAllBytes(SettingsFile, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("{ \"model\": \"opus\" }")]);

        // Act
        IReadOnlyList<SetupLine> lines = Merge();

        // Assert
        lines.Select(l => l.Outcome).Should().Equal(SetupOutcome.Done, SetupOutcome.Done);
        byte[] written = File.ReadAllBytes(SettingsFile);
        written.AsSpan().StartsWith(Encoding.UTF8.Preamble).Should().BeFalse();
        JsonNode.Parse(written)!["model"]!.GetValue<string>().Should().Be("opus");
    }

    [Fact]
    public void Apply_DenyRuleDifferingInCaseOrSpacing_OursAdded()
    {
        // Arrange
        Seed("{ \"permissions\": { \"deny\": [ \"Bash(Zyggy secret*)\", \"Bash(zyggy  context --flush*)\" ] } }");

        // Act
        Merge();

        // Assert
        JsonNode.Parse(File.ReadAllText(SettingsFile))!["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(
            "Bash(Zyggy secret*)", "Bash(zyggy  context --flush*)", "Bash(zyggy secret*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)");
    }

    [Fact]
    public void Apply_OwnerTextWithNonAsciiAndQuotes_KeptReadable()
    {
        // Arrange
        Seed("{ \"statusLine\": \"café <it's> & more\" }");

        // Act
        Merge();

        // Assert
        File.ReadAllText(SettingsFile).Should().Contain("\"statusLine\": \"café <it's> & more\"");
    }

    private static void ReadOnly(string file, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
        }
        else
        {
            File.SetUnixFileMode(file, readOnly
                ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private IReadOnlyList<SetupLine> Merge() => ClaudeSettingsMerge.Apply(SettingsFile, Patterns, SetupTestFiles.Stamp);

    private void Seed(string json)
    {
        Directory.CreateDirectory(ClaudeDir);
        File.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
