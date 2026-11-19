using System.Text.Json.Nodes;

using Zyggy.Core.Configuration;

namespace Zyggy.Core.Tests.Configuration;

// AC-4: each key accepts a valid value and refuses each invalid one with exit 2 and its line; absent memory variables are exit 3 for
// the outbound key only; removing a work machine prints the read-root line. Spec 55 slice 3.
public sealed class SettingsVerbRuleTests : IDisposable
{
    private readonly SettingsTree _tree = new();

    public SettingsVerbRuleTests() => _tree.WithMemory();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Set_Project_FoundByTheScan_WrittenWithBeforeAndAfter()
    {
        // Arrange
        _tree.Write(extra: $"\"devRoots\": [\"{SettingsTree.J(_tree.Parent)}\"], \"discovery\": {{ \"exclude\": [\"home\"] }}");

        // Act
        var (exit, output, _) = await _tree.RunAsync("set", "supervision.project", "instance");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be("settings: supervision.project: (none) -> instance\nsettings: the node applies this within a minute\n");
        JsonNode.Parse(File.ReadAllText(_tree.Config))!["supervision"]!["project"]!.GetValue<string>().Should().Be("instance");
    }

    [Fact]
    public async Task Set_Project_UnknownName_Exit2NamingTheProjectsThatExist()
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("set", "supervision.project", "nope");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: no such project on this machine: nope (projects: instance)\n");
    }

    [Fact]
    public async Task Add_DevRoots_MissingFolder_Exit2NoSuchFolder()
    {
        // Arrange
        _tree.WriteCentralShaped();
        string missing = Path.Combine(_tree.Root, "missing");

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "devRoots", missing);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: no such folder: " + missing + "\n");
    }

    [Fact]
    public async Task Add_DevRoots_RelativePathOfAnExistingFolder_Exit2NoSuchFolder()
    {
        // Arrange: "." always exists relative to the working directory, so only the absolute-path rule refuses it.
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "devRoots", ".");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: no such folder: .\n");
    }

    [Fact]
    public async Task Add_DevRoots_ExistingFolder_WrittenAndPublishesItsProjects()
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, output, _) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be(
            $"settings: devRoots: {_tree.Parent} -> {_tree.Parent}, {_tree.Other}\n"
            + "settings: publishes: calizr\n"
            + "settings: the node applies this within a minute\n");
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task Add_Exclude_GlobWithASeparator_Exit2(string glob)
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "discovery.exclude", glob);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: not a folder-name glob: " + glob + "\n");
    }

    [Fact]
    public async Task Add_Exclude_ThatLeavesOutTheSupervisionProject_Exit2WouldNoLongerBeFound()
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "discovery.exclude", "inst*");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: supervision.project instance would no longer be found\n");
    }

    [Fact]
    public async Task Remove_DevRoots_TheSupervisionProjectsRoot_Exit2WouldNoLongerBeFound()
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("remove", "devRoots", _tree.Parent);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: supervision.project instance would no longer be found\n");
    }

    [Fact]
    public async Task Add_WorkMachine_UnknownOnTheBus_Exit2()
    {
        // Arrange
        _tree.WriteCentralShaped($"\"outboundPaths\": [\"{SettingsTree.J(_tree.Business)}\"]");

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.workMachines", "office-pc");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: no such machine on the bus: office-pc\n");
    }

    [Fact]
    public async Task Add_WorkMachine_Central_Exit2()
    {
        // Arrange
        _tree.WriteCentralShaped($"\"outboundPaths\": [\"{SettingsTree.J(_tree.Business)}\"]");

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.workMachines", "central");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: central is never a work machine\n");
    }

    [Fact]
    public async Task Add_WorkMachine_BeforeAnOutboundFolder_Exit2()
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.workMachines", "work-laptop");

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: name an outbound folder first\n");
    }

    [Fact]
    public async Task Add_WorkMachine_KnownWithAnOutboundFolder_Written()
    {
        // Arrange
        _tree.WriteCentralShaped($"\"outboundPaths\": [\"{SettingsTree.J(_tree.Business)}\"]");

        // Act
        var (exit, output, _) = await _tree.RunAsync("add", "supervision.workMachines", "work-laptop");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().StartWith("settings: supervision.workMachines: (none) -> work-laptop\n");
    }

    [Fact]
    public async Task Remove_WorkMachine_PrintsTheReadRootLine()
    {
        // Arrange
        _tree.WriteCentralShaped($"\"outboundPaths\": [\"{SettingsTree.J(_tree.Business)}\"], \"workMachines\": [\"work-laptop\"]");

        // Act
        var (exit, output, _) = await _tree.RunAsync("remove", "supervision.workMachines", "work-laptop");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be(
            "settings: supervision.workMachines: work-laptop -> (none)\n"
            + "settings: work-laptop is no longer a work machine: its questions are answered without the read root\n"
            + "settings: the node applies this within a minute\n");
    }

    [Fact]
    public async Task Remove_LastOutboundFolder_WhileAWorkMachineIsListed_Exit2()
    {
        // Arrange
        _tree.WriteCentralShaped($"\"outboundPaths\": [\"{SettingsTree.J(_tree.Business)}\"], \"workMachines\": [\"work-laptop\"]");

        // Act
        var (exit, _, error) = await _tree.RunAsync("remove", "supervision.outboundPaths", _tree.Business);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: the last outbound folder stays while supervision.workMachines is not empty\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("topics")]
    public async Task Add_Outbound_BusinessOrBelow_Written(string below)
    {
        // Arrange
        _tree.WriteCentralShaped();
        string folder = Path.Combine(_tree.Business, below);

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.outboundPaths", folder);

        // Assert
        error.Should().BeEmpty();
        exit.Should().Be(SettingsVerb.Ok);
    }

    [Theory]
    [InlineData("memory-root")]
    [InlineData("principal")]
    [InlineData(".git")]
    [InlineData("private")]
    [InlineData("inbox")]
    [InlineData("daily")]
    [InlineData("archive")]
    [InlineData("outside")]
    public async Task Add_Outbound_NotUnderTheBusinessMemory_Exit2(string which)
    {
        // Arrange
        _tree.WriteCentralShaped();
        string folder = which switch
        {
            "memory-root" => _tree.MemoryRoot,
            "principal" => _tree.Principal,
            "outside" => _tree.Other,
            _ => Path.Combine(_tree.Principal, which),
        };

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.outboundPaths", folder);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: outbound folders lie under the business memory\n");
    }

    [Fact]
    public async Task Add_Outbound_LinkUnderBusinessThatLeavesIt_Exit2()
    {
        // Arrange
        _tree.WriteCentralShaped();
        string link = Path.Combine(_tree.Business, "shortcut");
        Assert.SkipUnless(
            SettingsTree.TryCreateDirectoryLink(link, Path.Combine(_tree.Principal, "private")),
            "the OS refused to create a directory link or junction here");

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.outboundPaths", link);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be("zyggy: outbound folders lie under the business memory\n");
    }

    [Fact]
    public async Task Add_Outbound_MemoryVariablesAbsent_Exit3NamingTheVariable()
    {
        // Arrange
        _tree.WriteCentralShaped();
        _tree.Environment.Clear();

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "supervision.outboundPaths", _tree.Business);

        // Assert
        exit.Should().Be(SettingsVerb.Configuration);
        error.Should().Be("zyggy: ZYGGY_MEMORY_ROOT is not set\n");
    }

    [Fact]
    public async Task Add_DevRoots_MemoryVariablesAbsent_StillWritten()
    {
        // Arrange
        _tree.WriteCentralShaped();
        _tree.Environment.Clear();

        // Act
        var (exit, _, _) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
    }

    [Theory]
    [InlineData("set", "claude.path", "x", "zyggy: unknown setting: claude.path\n")]
    [InlineData("set", "devRoots", "x", "zyggy: devRoots takes add or remove\n")]
    [InlineData("add", "supervision.project", "instance", "zyggy: supervision.project takes set\n")]
    [InlineData("remove", "supervision.project", "instance", "zyggy: supervision.project takes set\n")]
    public async Task Change_UnknownKeyOrWrongForm_Exit2(string form, string key, string value, string expected)
    {
        // Arrange
        _tree.WriteCentralShaped();

        // Act
        var (exit, _, error) = await _tree.RunAsync(form, key, value);

        // Assert
        exit.Should().Be(SettingsVerb.Refused);
        error.Should().Be(expected);
    }

    [Fact]
    public async Task Change_Unattended_Exit5()
    {
        // Arrange
        _tree.WriteCentralShaped();
        _tree.Environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Unattended);
        error.Should().Be("zyggy: refused: settings are changed only in an attended session\n");
    }

    [Fact]
    public async Task Change_NotCentral_Exit3()
    {
        // Arrange
        _tree.Write(machine: "home-laptop");

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Configuration);
        error.Should().Be("zyggy: settings are changed by this verb only on Central\n");
    }
}
