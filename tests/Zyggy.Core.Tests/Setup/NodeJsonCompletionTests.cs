using System.Text.Json.Nodes;

using Zyggy.Core.Envelope;
using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 AC-7..AC-12 and Edge Cases: an old <c>node.json</c> completed backup-first — only <c>claude.path</c> and <c>signWith</c>.</summary>
public sealed class NodeJsonCompletionTests : IDisposable
{
    private readonly SetupTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Apply_NoClaudeSection_ClaudeExeOnPath_BackupWrittenPathAddedForwardSlashes_EveryOtherPropertyJsonEqual()
    {
        // Arrange
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        byte[] original = File.ReadAllBytes(config);
        _files.FakeClaudeExe();
        string exe = ClaudeLocator.Find(_files.Environment(), _files.UserProfile)!;

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), exe);

        // Assert
        File.ReadAllBytes(_files.BackupPath).Should().Equal(original);
        JsonObject written = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        written["claude"]!["path"]!.GetValue<string>().Should().Be(SetupTestFiles.Slashes(Path.GetFullPath(Path.Combine(_files.Bin, "claude.exe"))));
        written["claude"]!["path"]!.GetValue<string>().Should().NotContain("\\");
        JsonObject before = JsonNode.Parse(original)!.AsObject();
        written.Remove("claude");
        JsonNode.DeepEquals(written, before).Should().BeTrue();
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Done, "added claude.path (backup " + _files.BackupPath + ")"));
        result.Configuration.Should().NotBeNull();
    }

    [Fact]
    public void Apply_NoClaudeSection_ClaudeExeOnlyInUserProfileLocalBin_Added()
    {
        // Arrange
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        string exe = _files.FakeClaudeInLocalBin();
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), found);

        // Assert
        result.Line.Outcome.Should().Be(SetupOutcome.Done);
        JsonNode.Parse(File.ReadAllText(config))!["claude"]!["path"]!.GetValue<string>().Should().Be(SetupTestFiles.Slashes(Path.GetFullPath(exe)));
    }

    [Fact]
    public void Apply_NoClaudeSection_OnlyClaudeCmdFound_NotAdded_AlreadyDoneWithTheRemedyDetail()
    {
        // Arrange
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        byte[] original = File.ReadAllBytes(config);
        _files.FakeClaudeExe("claude.cmd");
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), found);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.AlreadyDone, "claude.path not set; no claude.exe found (the installer's claude.path pre-flight names the remedy)"));
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Fact]
    public void Apply_NoSignWith_OptionGiven_AddedWithBackup()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson(extra: $"\"claude\": {{ \"path\": \"{SetupTestFiles.Slashes(exe)}\" }}"));

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(KeyId.Parse("acme/2")), exe);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Done, "added signWith (backup " + _files.BackupPath + ")"));
        JsonNode.Parse(File.ReadAllText(config))!["signWith"]!.GetValue<string>().Should().Be("acme/2");
        result.Configuration!.SignWith.Should().Be(KeyId.Parse("acme/2"));
    }

    [Fact]
    public void Apply_NoSignWith_NoOption_FailedExitThreeRemedy_FileUntouchedNoBackup()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson());
        byte[] original = File.ReadAllBytes(config);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), exe);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Failed, "signWith missing; run install.ps1 again with -SignWith acme/<n>"));
        result.Configuration.Should().BeNull();
        SetupExit.Worst([result.Line]).Should().Be(SetupExit.NodeConfig);
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("globex", "home-laptop", "node.json names tenant globex and machine home-laptop; the installer was given acme and home-laptop")]
    [InlineData("acme", "work-laptop", "node.json names tenant acme and machine work-laptop; the installer was given acme and home-laptop")]
    public void Apply_TenantOrMachineDiffers_FailedBothValuesNamed_FileUntouchedNoBackup(string tenant, string machine, string detail)
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: tenant + "/1", tenant: tenant, machine: machine));
        byte[] original = File.ReadAllBytes(config);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(KeyId.Parse("acme/1")), exe);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Failed, detail));
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Fact]
    public void Apply_NotValidJson_FailedReaderMessage_FileUntouchedNoBackup()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson("{ \"tenant\": \"acme\", ");
        byte[] original = File.ReadAllBytes(config);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(KeyId.Parse("acme/1")), exe);

        // Assert
        result.Line.Outcome.Should().Be(SetupOutcome.Failed);
        result.Line.Detail.Should().StartWith("node.json is not valid JSON: ");
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Fact]
    public void Apply_ReaderRefusesAfterCompletion_FailedReaderMessage_FileUntouchedNoBackup()
    {
        // Arrange: the bus section is missing — the reader refuses the completed file, so it never replaces the original (AC-10)
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", withBus: false));
        byte[] original = File.ReadAllBytes(config);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), exe);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Failed, "bus: bus section missing"));
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(config)!).Should().Equal(config);
    }

    [Fact]
    public void Apply_AlreadyComplete_AlreadyDone_NoWriteNoBackup_LastWriteTimeUnchanged()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", extra: $"\"claude\": {{ \"path\": \"{SetupTestFiles.Slashes(exe)}\" }}"));
        byte[] original = File.ReadAllBytes(config);
        DateTime stamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(config, stamp);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(KeyId.Parse("acme/1")), exe);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.AlreadyDone, null));
        File.ReadAllBytes(config).Should().Equal(original);
        File.GetLastWriteTimeUtc(config).Should().Be(stamp);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Fact]
    public void Apply_DefaultedKeysAbsent_NeverAdded()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson().Replace("  \"secrets\": { \"store\": \"file\", \"root\": \"" + SetupTestFiles.Slashes(_files.SecretsRoot) + "\" }\n", "  \"note\": \"spike\"\n", StringComparison.Ordinal));

        // Act
        NodeJsonCompletion.Apply(_files.Request(KeyId.Parse("acme/1")), exe);

        // Assert
        JsonObject written = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        written.Select(p => p.Key).Should().NotContain(["secrets", "workHours", "devRoots", "log", "maxConcurrentJobs", "archive"]);
        written.Select(p => p.Key).Should().Contain(["claude", "signWith"]);
    }

    [Fact]
    public void Apply_ClaudePathNamesMissingFile_ClaudeExeFound_ReplacedWithOldAndNewInTheLine()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string old = SetupTestFiles.Slashes(Path.Combine(_files.Root, "gone", "claude.exe"));
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", extra: $"\"claude\": {{ \"path\": \"{old}\", \"maxTurns\": 40 }}"));
        string expected = SetupTestFiles.Slashes(Path.GetFullPath(exe));

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), expected);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.Done, $"claude.path {old} did not exist; set to {expected} (backup {_files.BackupPath})"));
        JsonNode claude = JsonNode.Parse(File.ReadAllText(config))!["claude"]!;
        claude["path"]!.GetValue<string>().Should().Be(expected);
        claude["maxTurns"]!.GetValue<int>().Should().Be(40);
    }

    [Fact]
    public void Apply_ClaudePathNamesMissingFile_NoneFound_LeftAsIs()
    {
        // Arrange
        string old = SetupTestFiles.Slashes(Path.Combine(_files.Root, "gone", "claude.exe"));
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", extra: $"\"claude\": {{ \"path\": \"{old}\" }}"));
        byte[] original = File.ReadAllBytes(config);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), claudeExe: null);

        // Assert
        result.Line.Should().Be(new SetupLine(SetupItem.NodeJson, SetupOutcome.LeftAsIs, $"claude.path {old} does not exist; no claude.exe found (the installer's claude.path pre-flight names the remedy)"));
        File.ReadAllBytes(config).Should().Equal(original);
        File.Exists(_files.BackupPath).Should().BeFalse();
    }

    [Fact]
    public void Apply_FileWithComments_BackupKeepsThem_RewrittenWithout_LineSaysSo()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string json = _files.SpikeJson(signWith: "acme/1").Replace("  \"tenant\"", "  // the owner's note\n  \"tenant\"", StringComparison.Ordinal);
        string config = _files.WriteNodeJson(json);

        // Act
        NodeJsonResult result = NodeJsonCompletion.Apply(_files.Request(), exe);

        // Assert
        result.Line.Detail.Should().Be("added claude.path (backup " + _files.BackupPath + "); comments kept only in the backup");
        File.ReadAllText(_files.BackupPath).Should().Contain("// the owner's note");
        File.ReadAllText(config).Should().NotContain("the owner's note");
    }

    [Fact]
    public void Apply_SecretsStoreFile_NotChanged()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        string config = _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", store: "file"));
        JsonNode before = JsonNode.Parse(File.ReadAllText(config))!["secrets"]!.DeepClone();

        // Act
        NodeJsonCompletion.Apply(_files.Request(), exe);

        // Assert
        JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(config))!["secrets"], before).Should().BeTrue();
    }
}
