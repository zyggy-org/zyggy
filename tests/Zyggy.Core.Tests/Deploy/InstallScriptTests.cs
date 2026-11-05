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
        names.Should().Equal("Tenant", "Machine", "SignWith", "BusLocalPath", "CorePath", "Source", "NodeJson", "Uninstall");
        block.Should().MatchRegex(@"\[Parameter\(Mandatory\)\]\s*\[string\]\s*\$Tenant");
        block.Should().MatchRegex(@"\[Parameter\(Mandatory\)\]\s*\[string\]\s*\$Machine");
        block.Should().Contain("$BusLocalPath = 'C:\\agents\\bus'");
        block.Should().Contain("$CorePath = 'C:\\agents\\zyggy-core'");
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
        Code.Should().Contain("GetFolderPath('LocalApplicationData')").And.Contain("Programs\\Zyggy");
    }

    [Fact]
    public void Script_SourceNotGiven_UsesTheScriptFolderWhenItHoldsZyggyExeElseTheReleaseRoot()
    {
        // Bugfix 2026-11-02: `$Source = $PSScriptRoot` pointed at deploy\windows of the unpacked release, where the binaries are not.
        // Act
        string block = ParamBlock().Match(Text).Groups["block"].Value;

        // Assert
        block.Should().NotContain("$PSScriptRoot", "the default is computed in the body");
        Code.Should().Contain("if ([string]::IsNullOrWhiteSpace($Source)) {");
        Code.Should().Contain("Test-Path (Join-Path $PSScriptRoot 'zyggy.exe')");
        Code.Should().Contain("Split-Path (Split-Path $PSScriptRoot -Parent) -Parent", "deploy\\windows -> the release root");
        Code.IndexOf("IsNullOrWhiteSpace($Source)", StringComparison.Ordinal).Should().BeLessThan(Code.IndexOf("Join-Path $Source 'SHA256SUMS'", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_LocalAppDataEmptyInTheProcess_PerUserFoldersComeFromGetFolderPathAndNodeJsonDefaultsInTheBody()
    {
        // Bugfix 2026-11-02: $env:LOCALAPPDATA was empty in a 5.1 process started from pwsh 7, and the param default
        // `Join-Path $env:LOCALAPPDATA ...` failed at binding before any step ran.
        // Act
        string block = ParamBlock().Match(Text).Groups["block"].Value;

        // Assert
        block.Should().NotContain("Join-Path").And.NotContain("$env:");
        Code.Should().NotContain("$env:LOCALAPPDATA").And.NotContain("$env:USERPROFILE");
        Code.Should().Contain("[Environment]::GetFolderPath('LocalApplicationData')").And.Contain("[Environment]::GetFolderPath('UserProfile')");
        Code.Should().Contain("if ([string]::IsNullOrWhiteSpace($NodeJson)) {");
        Code.IndexOf("IsNullOrWhiteSpace($NodeJson)", StringComparison.Ordinal).Should().BeLessThan(Code.IndexOf("if ($Uninstall)", StringComparison.Ordinal), "the uninstall branch names node.json too");
    }

    [Fact]
    public void Script_NodeJsonWithoutClaudeOrSignWith_ReadsOptionalPropertiesWithoutAStrictModeCrash()
    {
        // Bugfix 2026-11-02: under Set-StrictMode -Version 2.0 `$config.claude.path` threw PropertyNotFoundStrict on a 0.8.1
        // node.json without a claude object, instead of reaching the script's own Fail with its remedy.
        // Act
        var directReads = Regex.Matches(Code, @"\$config\.[A-Za-z]").Select(m => m.Value).ToList();

        // Assert
        Code.Should().Contain("Set-StrictMode -Version 2.0");
        directReads.Should().BeEmpty("every node.json property is read through PSObject.Properties");
        Code.Should().Contain("PSObject.Properties");
        Code.Should().Contain("Fail \"claude.path ($claudePath) does not exist\"");
    }

    [Fact]
    public void Script_StatusStep_PrintsTheFirstNodeLineNotALogLine()
    {
        // Bugfix 2026-11-02: PowerShell 5.1 renders a merged stderr line as `zyggy.exe : info: ...`, which slipped past the
        // `^\s*info:` filter and was printed as the status line.
        // Act
        string statusLine = DeployAssets.Lines(Code).Single(l => l.Contains("@('status'", StringComparison.Ordinal));

        // Assert
        statusLine.Should().Contain("-match '^node\\s'").And.Contain("Select-Object -First 1");
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
    [InlineData(".local\\bin\\claude.exe")]
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
        string processes = Regex.Match(Code, @"function Stop-NodeProcesses\b.*?\n\}", RegexOptions.Singleline).Value;

        // Assert
        helper.Should().NotBeEmpty("the stop lives in one helper");
        processes.Should().NotBeEmpty("the wait-then-force pattern lives in one helper (plan 42 Step 7)");
        stopLines.Should().ContainSingle("every stop goes through the helper");
        helper.Should().Contain(stopLines[0]);
        helper.Should().Contain("Get-Process -Name zyggy-node").And.Contain("$nodeExe").And.Contain("Stop-NodeProcesses");
        processes.Should().Contain("Wait-Process").And.Contain("Stop-Process -Force");
        DeployAssets.Lines(Code).Count(l => l.TrimStart().StartsWith("Stop-Node ", StringComparison.Ordinal)).Should().Be(2, "the uninstall branch and the upgrade both stop through it");
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
        DeployAssets.Lines(Code).Count(l => l.Contains("Invoke-Native ", StringComparison.Ordinal) && !l.TrimStart().StartsWith("function", StringComparison.Ordinal)).Should().Be(3, "git --version, setup and status");
    }

    [Fact]
    public void Script_Step2_RemovesAnOlderZyggyFolderFromTheUserPathOnlyWhenItHoldsOnlyZyggyFiles_NeverDeletesIt()
    {
        // Spec 42 AC-1/AC-2, R3: the user PATH only, only a zyggy*-only folder, files kept; other copies get one warning line.
        // Act
        string oldDirs = DeployAssets.Lines(Code).Single(l => l.TrimStart().StartsWith("$oldDirs =", StringComparison.Ordinal));
        Match removal = Regex.Match(Code, @"try \{\n(?<body>(?:(?!\} catch \{).)*?)\} catch \{\n\s*Step ""warning: could not remove \$dir from the user PATH: ", RegexOptions.Singleline);
        var removeItems = DeployAssets.Lines(Code).Where(l => l.Contains("Remove-Item", StringComparison.Ordinal)).ToList();

        // Assert
        oldDirs.Should().Contain("$userPath").And.Contain("'zyggy.exe'").And.Contain("'zyggy-node.exe'").And.Contain("$programs");
        Code.Should().Contain("-notlike 'zyggy*'");
        removal.Success.Should().BeTrue("the user-PATH write sits inside a try whose catch only warns");
        removal.Groups["body"].Value.Should().Contain("SetEnvironmentVariable('Path'").And.Contain("'User')")
            .And.Contain("Step \"removed $dir from the user PATH (an older zyggy; its files are kept - delete the folder by hand when you no longer need it)\"");
        Code.Should().Contain("Step \"warning: $dir holds an older zyggy next to other files and shadows the new one; move the zyggy files out or edit the user PATH by hand (rundll32 sysdm.cpl,EditEnvironmentVariables)\"");
        Code.Should().Contain("Step \"warning: $dir holds an older zyggy and is not on the user PATH (the machine PATH needs whoever administers this machine)\"");
        Code.Should().NotContain("'Machine'", "a machine-PATH copy is found as a session entry that is not on the user PATH (plan 42 R3)");
        removeItems.Should().NotContain(l => l.Contains("$dir", StringComparison.Ordinal) || l.Contains("$old", StringComparison.Ordinal), "nothing is deleted");
    }

    [Fact]
    public void Script_Step2_StopsAnOldFolderNodeThroughTheWaitThenForceHelper_BeforeTheCopy()
    {
        // Spec 42 AC-3: an older zyggy-node from an old folder would hold port 4711.
        // Act
        string processes = Regex.Match(Code, @"function Stop-NodeProcesses\b.*?\n\}", RegexOptions.Singleline).Value;
        string stopNode = Regex.Match(Code, @"function Stop-Node\b.*?\n\}", RegexOptions.Singleline).Value;
        string outsideHelpers = Code.Replace(processes, string.Empty).Replace(stopNode, string.Empty);
        var lines = DeployAssets.Lines(Code).ToList();
        int oldStop = lines.FindIndex(l => l.TrimStart().StartsWith("Stop-NodeProcesses", StringComparison.Ordinal) && !stopNode.Contains(l, StringComparison.Ordinal));
        int firstCopy = lines.FindIndex(l => l.Contains("Copy-Item", StringComparison.Ordinal));

        // Assert
        processes.Should().NotBeEmpty();
        DeployAssets.Lines(outsideHelpers).Should().NotContain(l => l.Contains("Wait-Process", StringComparison.Ordinal) || l.Contains("Stop-Process -Force", StringComparison.Ordinal), "the helper is the only place with the pattern");
        stopNode.Should().Contain("Stop-NodeProcesses");
        oldStop.Should().BePositive("the old-folder stop calls the helper too");
        oldStop.Should().BeLessThan(firstCopy, "the old node is stopped before the copy");
        lines.Where(l => l.Contains("Stop-Process -Id", StringComparison.Ordinal)).Should().OnlyContain(l => l.TrimStart().StartsWith("Fail ", StringComparison.Ordinal), "a manual stop is only ever a remedy");
        Code.Should().Contain("stopped the older zyggy-node in $dir (it would hold port 4711)").And.Contain("could not be stopped");
    }

    [Fact]
    public void Script_Step2_PutsTheProgramsFolderFirstInTheSessionPath_AndTheReopenLineIsLastOnSuccess()
    {
        // Spec 42 AC-4.
        // Act
        string incomplete = Regex.Match(Code, @"if \(\$setupIncomplete\) \{.*?\n\}", RegexOptions.Singleline).Value;
        var steps = DeployAssets.Lines(Code.Replace(incomplete, string.Empty)).Where(l => l.TrimStart().StartsWith("Step ", StringComparison.Ordinal)).ToList();
        int session = Code.IndexOf("$env:Path = $programs + ';'", StringComparison.Ordinal);

        // Assert
        session.Should().BePositive();
        session.Should().BeGreaterThan(Code.IndexOf("Step \"removed $dir from the user PATH", StringComparison.Ordinal), "the session PATH drops what was removed");
        session.Should().BeLessThan(Code.IndexOf("$busParent =", StringComparison.Ordinal), "it is part of step 2");
        Code.Should().Contain("session PATH: $programs first");
        steps[^1].Should().Contain("reopen: terminals, VS Code windows and Claude Code sessions opened before this install still have the old PATH and environment - close and reopen them (VS Code: all windows)");
        incomplete.Should().Contain("Step 'incomplete - see the setup lines above'").And.Contain("exit 1");
        Code.IndexOf(incomplete, StringComparison.Ordinal).Should().BeGreaterThan(Code.IndexOf("reopen: terminals", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_CallsSetupOnceThroughInvokeNativeByAbsolutePath_WithTheOptions_AndBranchesOnThreeFourAndSix()
    {
        // Spec 42 AC-5.
        // Act
        var lines = DeployAssets.Lines(Code).ToList();
        string setupArgs = lines.Single(l => l.Contains("'setup'", StringComparison.Ordinal));
        string call = lines.Single(l => l.Contains("$setupArgs", StringComparison.Ordinal) && l.Contains("Invoke-Native", StringComparison.Ordinal));
        int callIndex = Code.IndexOf(call, StringComparison.Ordinal);
        int register = Code.IndexOf("Register-ScheduledTask", StringComparison.Ordinal);

        // Assert
        setupArgs.Should().Contain("$setupArgs = @('setup', '--config', $NodeJson, '--tenant', $Tenant, '--machine', $Machine, '--core-path', $CorePath)");
        call.Trim().Should().Be("$setupOutput = Invoke-Native $zyggyExe $setupArgs");
        Code.Should().MatchRegex(@"if \(-not \[string\]::IsNullOrWhiteSpace\(\$SignWith\)\) \{\n\s*\$setupArgs \+= @\('--sign-with', \$SignWith\)\n", "--sign-with only when -SignWith was given");
        Code.Should().MatchRegex(@"if \(\$setupExit -eq 3\) \{\n\s*Fail ").And.MatchRegex(@"if \(\$setupExit -eq 4\) \{\n\s*Fail ");
        Code.IndexOf("$setupExit -eq 3", StringComparison.Ordinal).Should().BeGreaterThan(callIndex).And.BeLessThan(register);
        Code.IndexOf("$setupExit -eq 4", StringComparison.Ordinal).Should().BeGreaterThan(callIndex).And.BeLessThan(register);
        Code.Should().Contain("$setupIncomplete = ($setupExit -eq 6)");
        Code.IndexOf("if ($setupIncomplete)", StringComparison.Ordinal).Should().BeGreaterThan(Code.IndexOf("Start-ScheduledTask", StringComparison.Ordinal));
        callIndex.Should().BeGreaterThan(Code.IndexOf("holds tenants", StringComparison.Ordinal), "setup runs after the checkout pre-flight")
            .And.BeLessThan(Code.IndexOf("$claudePath =", StringComparison.Ordinal), "the claude.path pre-flight reads what setup completed");
        Code.Should().Contain("'incomplete - see the setup lines above'");
    }

    [Fact]
    public void Script_NoSecretListParsing()
    {
        // Spec 42 AC-5: setup owns the secret report.
        Code.Should().NotContain("'secret', 'list'").And.NotContain("\\ssigns").And.NotContain("file secret store selected");
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
