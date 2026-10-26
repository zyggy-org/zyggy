using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Deploy;

/// <summary>
/// Spec 10 AC-2, static invariants over <c>deploy/windows/install.ps1</c> (rule R1: shell that runs before any binary is trusted,
/// on a laptop where the owner is not an administrator): it never elevates, never takes a password, never writes machine-wide,
/// downloads nothing, checks the checksums before copying, and registers the logon task exactly as the contract table says.
/// </summary>
public sealed partial class InstallScriptTests
{
    private const string Script = "windows/install.ps1";

    private static string Text => DeployAssets.Text(Script);

    // Lines that are only a comment carry no behaviour (a `#Requires` line does, and is kept); the header's wording is asserted separately.
    private static string Code => string.Join("\n", DeployAssets.Lines(Text).Where(l => !l.TrimStart().StartsWith('#') || l.TrimStart().StartsWith("#Requires", StringComparison.Ordinal)));

    [Theory]
    [InlineData("#Requires -RunAsAdministrator")]
    [InlineData("-Verb RunAs")]
    [InlineData("RunAs")]
    [InlineData("New-Service")]
    [InlineData("Set-Service")]
    [InlineData("sc.exe")]
    [InlineData("sc create")]
    [InlineData("LocalSystem")]
    [InlineData("LocalService")]
    [InlineData("NetworkService")]
    [InlineData("NT AUTHORITY")]
    [InlineData("S4U")]
    [InlineData("ServiceAccount")]
    [InlineData("-LogonType Password")]
    public void Script_NeverElevates_NoRequiresNoRunAsNoServiceCmdletNoServiceAccount(string forbidden)
    {
        Code.Should().NotContain(forbidden);
        Text.Should().Contain("no step elevates");
    }

    [Theory]
    [InlineData("Get-Credential")]
    [InlineData("PSCredential")]
    [InlineData("-Password")]
    [InlineData("SecureString")]
    [InlineData("Read-Host")]
    public void Script_NeverTakesAPassword(string forbidden)
    {
        Code.Should().NotContain(forbidden);
    }

    [Fact]
    public void Script_ParametersMatchTheContract()
    {
        // Act
        string block = ParamBlock().Match(Text).Groups["block"].Value;
        var names = Regex.Matches(block, @"\]\s*\$(?<name>[A-Za-z]+)").Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToList();

        // Assert
        names.Should().Equal("Tenant", "Machine", "SignWith", "BusLocalPath", "Source", "NodeJson", "Uninstall");
        block.Should().MatchRegex(@"\[Parameter\(Mandatory\)\]\s*\[string\]\s*\$Tenant");
        block.Should().MatchRegex(@"\[Parameter\(Mandatory\)\]\s*\[string\]\s*\$Machine");
        block.Should().Contain("$BusLocalPath = 'C:\\agents\\bus'");
        block.Should().MatchRegex(@"\[switch\]\s*\$Uninstall");
    }

    [Theory]
    [InlineData("Register-ScheduledTask")]
    [InlineData("-TaskName 'zyggy-node'")]
    [InlineData("-Force")]
    [InlineData("-LogonType Interactive")]
    [InlineData("-RunLevel Limited")]
    [InlineData("-AtLogOn")]
    [InlineData("PT30S")]
    [InlineData("-Hidden")]
    [InlineData("-MultipleInstances IgnoreNew")]
    [InlineData("-ExecutionTimeLimit")]
    [InlineData("-RestartCount 3")]
    [InlineData("-RestartInterval")]
    [InlineData("New-TimeSpan -Minutes 1")]
    [InlineData("-AllowStartIfOnBatteries")]
    [InlineData("-DontStopIfGoingOnBatteries")]
    [InlineData("-StartWhenAvailable")]
    [InlineData("Start-ScheduledTask")]
    [InlineData("Stop-ScheduledTask")]
    [InlineData("Unregister-ScheduledTask")]
    public void Script_RegistersTheTaskAsTheContractTable(string token)
    {
        Code.Should().Contain(token);
        Code.Should().NotContain("-TaskPath");
    }

    [Fact]
    public void Script_ActionRunsTheNodeHiddenWithConfigAndGcmInteractiveNeverInThatProcessOnly()
    {
        // Act
        string action = DeployAssets.Lines(Code).Single(l => l.Contains("New-ScheduledTaskAction", StringComparison.Ordinal));
        var argumentLines = DeployAssets.Lines(Code).Where(l => l.Contains("GCM_INTERACTIVE", StringComparison.Ordinal)).ToList();

        // Assert
        action.Should().Contain("-Execute 'powershell.exe'");
        string arguments = string.Join("\n", DeployAssets.Lines(Code).Where(l => l.Contains("-NoProfile", StringComparison.Ordinal)));
        arguments.Should().Contain("-NoProfile").And.Contain("-NonInteractive").And.Contain("-WindowStyle Hidden").And.Contain("-ExecutionPolicy Bypass")
            .And.Contain("GCM_INTERACTIVE='never'").And.Contain("zyggy-node.exe").And.Contain("--config").And.Contain("LASTEXITCODE");
        argumentLines.Should().ContainSingle("GCM_INTERACTIVE is set only inside the task's own command");
        argumentLines[0].Should().Contain("-NoProfile");
        Code.Should().NotContain("SetEnvironmentVariable('GCM_INTERACTIVE'").And.NotContain("conhost");
    }

    [Theory]
    [InlineData("ProgramFiles")]
    [InlineData("Program Files")]
    [InlineData("HKLM")]
    [InlineData("HKEY_LOCAL_MACHINE")]
    [InlineData("C:\\Windows")]
    [InlineData("'Machine')")]
    public void Script_WritesNothingMachineWide(string forbidden)
    {
        Code.Should().NotContain(forbidden);
        Code.Should().Contain("'User')");
        Code.Should().Contain("$env:LOCALAPPDATA").And.Contain("Programs\\Zyggy");
    }

    [Theory]
    [InlineData("Invoke-WebRequest")]
    [InlineData("Invoke-RestMethod")]
    [InlineData("curl")]
    [InlineData("wget")]
    [InlineData("Start-BitsTransfer")]
    [InlineData("WebClient")]
    [InlineData("http://")]
    public void Script_DownloadsNothing(string forbidden)
    {
        Code.Should().NotContain(forbidden);
        Regex.Matches(Code, "https://[^\"' ]*").Select(m => m.Value).Should().OnlyContain(u => u == "https://github.com/zyggy-org/zyggy-bus.git", "the bus repository's URL is the only https: the template's remote and the clone remedy");
        Code.Should().Contain("git clone https://github.com/zyggy-org/zyggy-bus.git");
    }

    [Fact]
    public void Script_ChecksSha256SumsBeforeCopying()
    {
        Code.Should().Contain("Get-FileHash").And.Contain("SHA256SUMS");
        Code.IndexOf("Get-FileHash", StringComparison.Ordinal).Should().BeLessThan(Code.IndexOf("Copy-Item", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("zyggy secret set hmac/")]
    [InlineData("--hex")]
    [InlineData("rotate-hmac.md")]
    [InlineData("core.autocrlf false")]
    [InlineData("git clone https://github.com/zyggy-org/zyggy-bus.git")]
    [InlineData("claude.path")]
    public void Script_PreflightNamesTheTwoRefusalRemedies(string token)
    {
        Text.Should().Contain(token);
    }

    [Fact]
    public void Script_RefusesPolicyWithTheStartupAlternative()
    {
        Text.Should().Contain("Access is denied").And.Contain("shell:startup");
    }

    [Fact]
    public void Script_UninstallLeavesBinariesConfigCheckoutAndSecrets()
    {
        // Act: the branch between `if ($Uninstall)` and its closing brace at column 0
        string branch = UninstallBranch().Match(Code).Value;

        // Assert
        branch.Should().Contain("Unregister-ScheduledTask");
        var removals = DeployAssets.Lines(branch).Where(l => l.Contains("Remove-Item", StringComparison.Ordinal)).ToList();
        removals.Should().NotContain(l => l.Contains("Programs\\Zyggy", StringComparison.Ordinal) || l.Contains("node.json", StringComparison.Ordinal)
            || l.Contains("$BusLocalPath", StringComparison.Ordinal) || l.Contains("secrets", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_EveryTaskStopWaitsForTheNodeProcessAndEndsASurvivor()
    {
        // Bugfix 2026-10-26: Stop-ScheduledTask ends the task's powershell.exe wrapper, not its zyggy-node.exe child; the upgrade's
        // Copy-Item then collided with the orphan and `Stop-ScheduledTask` by hand left a node running. One helper owns the stop.
        // Act
        var stopLines = DeployAssets.Lines(Code).Where(l => l.Contains("Stop-ScheduledTask", StringComparison.Ordinal)).ToList();
        string helper = Regex.Match(Code, @"function Stop-Node\b.*?\n\}", RegexOptions.Singleline).Value;

        // Assert
        helper.Should().NotBeEmpty("the stop lives in one helper");
        stopLines.Should().ContainSingle("every stop goes through the helper");
        helper.Should().Contain(stopLines[0]);
        helper.Should().Contain("Get-Process -Name zyggy-node").And.Contain("$nodeExe").And.Contain("Wait-Process").And.Contain("Stop-Process -Force");
        DeployAssets.Lines(Code).Count(l => l.TrimStart().StartsWith("Stop-Node", StringComparison.Ordinal)).Should().Be(2, "the uninstall branch and the upgrade both stop through it");
    }

    [Fact]
    public void Script_NativeOutputIsReadThroughOneHelperThatNeverTurnsStderrIntoATerminatingError()
    {
        // Bugfix 2026-10-26: under $ErrorActionPreference = 'Stop', `& exe 2>&1` makes PowerShell 5.1 throw NativeCommandError on the
        // first stderr line; zyggy writes its info log there, so step 7 died after the task had started. One helper reads native output.
        // Act
        string helper = Regex.Match(Code, @"function Invoke-Native\b.*?\n\}", RegexOptions.Singleline).Value;
        var mergesOutsideHelper = DeployAssets.Lines(Code.Replace(helper, string.Empty)).Where(l => l.Contains("2>&1", StringComparison.Ordinal) || l.Contains("2>$null", StringComparison.Ordinal)).ToList();

        // Assert
        helper.Should().NotBeEmpty("native output is read in one place");
        helper.Should().Contain("$ErrorActionPreference = 'Continue'").And.Contain("2>&1").And.Contain("Out-String");
        mergesOutsideHelper.Should().BeEmpty("no other line merges or discards a native command's stderr");
        DeployAssets.Lines(Code).Count(l => l.Contains("Invoke-Native ", StringComparison.Ordinal) && !l.TrimStart().StartsWith("function", StringComparison.Ordinal)).Should().Be(3, "git --version, secret list and status");
    }

    [Theory]
    [InlineData("??")]
    [InlineData("?.")]
    [InlineData(" && ")]
    [InlineData(" || ")]
    [InlineData("-Parallel")]
    public void Script_PowerShell51Only_NoSevenOnlySyntax(string forbidden)
    {
        // PowerShell 5.1 has none of these; the ternary is not searched (plan 10 A2 states the gap)
        Code.Should().NotContain(forbidden);
    }

    [Fact]
    public void Script_LfNoTrailingWhitespaceNoSecretNoIdentifier()
    {
        // Act
        byte[] bytes = DeployAssets.Bytes(Script);

        // Assert
        Text.Should().NotContain("\r");
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 without BOM");
        DeployAssets.Lines(Text).Where(l => l.Length > 0 && char.IsWhiteSpace(l[l.Length - 1])).Should().BeEmpty("no trailing whitespace");
        Text.Should().NotContain("/home/").And.NotContain("C:\\Users\\");
        Text.Should().NotContainEquivalentOf(DeployAssets.OwnerNeedle);
        SecretSweep.AssertAbsent([Text]);
        Encoding.UTF8.GetString(bytes).Should().Be(Text);
    }

    [GeneratedRegex(@"param\s*\((?<block>.*?)\n\)", RegexOptions.Singleline)]
    private static partial Regex ParamBlock();

    [GeneratedRegex(@"if \(\$Uninstall\) \{.*?\n\}", RegexOptions.Singleline)]
    private static partial Regex UninstallBranch();
}
