# install.ps1 — the Zyggy node on a Windows laptop, per user, without administrator rights (deliverable 10, founding spec §10).
#
# Shell under rule R1, with the reason: this script runs before any Zyggy binary is trusted, it drives the user's own
# Task Scheduler and user environment, and it cannot be unit-tested by the rules of this repository — its invariants are
# checked statically by tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs. Everything it does is per user: no step elevates
# and no step asks to. The owner is not an administrator on every laptop.
#
# What it does, in order (each step prints one line; the first failure stops with the remedy):
#   1. verifies SHA256SUMS for zyggy.exe and zyggy-node.exe when the file is next to them;
#   2. stops the logon task if it runs and waits for the node process to end (the upgrade path), stops an older zyggy-node
#      running from an older zyggy folder on the user PATH, copies both binaries to %LOCALAPPDATA%\Programs\Zyggy, adds that
#      folder to the user PATH once, takes an older zyggy folder off the user PATH when it holds only zyggy* files (its files
#      are kept; any other copy gets a warning line), and puts the programs folder first in this session's PATH (deliverable 42);
#   3. creates the parent of the bus checkout path;
#   4. writes node.json from the template below when absent (else leaves it untouched);
#   5. pre-flight: git, the checkout and its tenants\<tenant> folder;
#   6. zyggy setup (deliverable 42): completes node.json (claude.path, signWith; backup first), clones or fast-forwards
#      zyggy-core at -CorePath, wires the user-scope Claude Code settings and the remember/archive/bus/discover skills, names every missing
#      secret with the command that sets it and a spike file store (rotate-hmac.md step 0); exit 3 or 4 stops here;
#   7. pre-flight: claude.path;
#   8. registers or updates the per-user logon task zyggy-node (interactive logon type, limited run level, hidden, restart on failure);
#   9. starts the task, prints the first line of zyggy status and the reopen line; a setup exit 6 then ends the run with exit 1.
# -Uninstall stops and unregisters the task only; the binaries, node.json, the checkout and the secrets stay.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tenant,
    [Parameter(Mandatory)] [string] $Machine,
    [string] $SignWith,
    [string] $BusLocalPath = 'C:\agents\bus',
    [string] $CorePath = 'C:\agents\zyggy-core',
    [string] $Source,
    [string] $NodeJson,
    [switch] $Uninstall
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# The per-user folders come from the shell API, not the environment: $env:LOCALAPPDATA was empty in a Windows PowerShell 5.1
# process started from pwsh 7 (seen 2026-11-02), and a param default built on it failed before any step ran.
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    Write-Host 'install: FAILED - this process has no per-user local application data folder'
    Write-Host 'install: remedy - run the script from a normal logged-on PowerShell window of this user'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($NodeJson)) {
    $NodeJson = Join-Path $localAppData 'zyggy\node.json'
}
# The release zip holds the binaries at its root and this script in deploy\windows: without -Source, the script's own folder
# when it holds zyggy.exe, else the release root two levels up.
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = $PSScriptRoot
    if (-not (Test-Path (Join-Path $PSScriptRoot 'zyggy.exe'))) {
        $Source = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    }
}

$taskName = 'zyggy-node'
$programs = Join-Path $localAppData 'Programs\Zyggy'
$nodeExe = Join-Path $programs 'zyggy-node.exe'
$zyggyExe = Join-Path $programs 'zyggy.exe'
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name

function Step([string] $line) {
    Write-Host "install: $line"
}

function Fail([string] $reason, [string] $remedy) {
    Write-Host "install: FAILED - $reason"
    Write-Host "install: remedy - $remedy"
    exit 1
}

# Reads an optional property of a parsed node.json object; under strict mode a missing property would otherwise throw
# PropertyNotFoundStrict before the script's own Fail and its remedy (seen 2026-11-02 on a 0.8.1 node.json without claude).
function Get-Optional($object, [string] $name) {
    if ($null -eq $object) {
        return $null
    }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-TaskOrNull {
    return Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}

# Stops the task and waits for the node itself: Task Scheduler ends the task's powershell.exe wrapper, and zyggy-node.exe can
# survive it (seen 2026-10-26: an orphan kept port 4711 and blocked the upgrade's copy). Only this user's own node is touched.
function Stop-Node([string] $why) {
    Stop-ScheduledTask -TaskName $taskName
    $node = @(Get-Process -Name zyggy-node -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $nodeExe })
    if ($node.Count -gt 0) {
        Stop-NodeProcesses $node
    }
    Step "stopped $taskName $why (node process ended)"
}

# Waits up to 30 s for the given zyggy-node processes to end, then ends the survivors; the one wait-then-force pattern.
function Stop-NodeProcesses($processes) {
    $processes | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    $processes | Where-Object { -not $_.HasExited } | Stop-Process -Force
}

# Runs a native command and returns its stdout and stderr as one string. Under $ErrorActionPreference = 'Stop' a merged stderr
# line is a terminating NativeCommandError in PowerShell 5.1 (zyggy writes its info log there); the preference is lowered here only.
function Invoke-Native([string] $exe, [string[]] $arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        return (& $exe @arguments 2>&1 | Out-String)
    } finally {
        $ErrorActionPreference = $previous
    }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole('Administrators')) {
    Step 'note: this shell is elevated; elevation was not needed and nothing here uses it'
}

if ($Uninstall) {
    $task = Get-TaskOrNull
    if ($null -eq $task) {
        Step "uninstall: no task named $taskName for $user; nothing to do"
    } else {
        if ($task.State -eq 'Running') {
            Stop-Node 'for the uninstall'
        }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Step "uninstall: unregistered $taskName"
    }
    Step "uninstall: left in place - $programs, $NodeJson, the checkout at $BusLocalPath and the secrets in the Credential Manager"
    exit 0
}

# 1. checksums, before anything is copied
$sums = Join-Path $Source 'SHA256SUMS'
foreach ($name in @('zyggy.exe', 'zyggy-node.exe')) {
    $file = Join-Path $Source $name
    if (-not (Test-Path $file)) {
        Fail "$name is not in $Source" 'point -Source at the unpacked release folder (zyggy.exe, zyggy-node.exe, SHA256SUMS)'
    }
    if (Test-Path $sums) {
        $expected = (Get-Content $sums | Where-Object { $_ -match "\s\*?$([regex]::Escape($name))$" } | Select-Object -First 1)
        if ($null -eq $expected) {
            Fail "SHA256SUMS has no line for $name" 'use the release folder as published; do not mix files from two releases'
        }
        $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
        if (-not $expected.ToLowerInvariant().StartsWith($actual)) {
            Fail "$name does not match SHA256SUMS" 'download the release again; a mismatch is a corrupt or tampered file'
        }
        Step "checksum ok: $name"
    } else {
        Step "no SHA256SUMS next to $name; checksum not verified"
    }
}

# 2. the binaries, per user, and the older copies (deliverable 42): an older zyggy folder on the user PATH shadowed the new one
$task = Get-TaskOrNull
if ($null -ne $task -and $task.State -eq 'Running') {
    Stop-Node 'for the upgrade'
}
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) { $userPath = '' }
$oldDirs = @(($userPath -split ';') | ForEach-Object { $_.TrimEnd('\') } | Where-Object { $_ -ne '' -and $_ -ne $programs -and ((Test-Path -LiteralPath (Join-Path $_ 'zyggy.exe')) -or (Test-Path -LiteralPath (Join-Path $_ 'zyggy-node.exe'))) } | Sort-Object -Unique)
$oldNodes = @(Get-Process -Name zyggy-node -ErrorAction SilentlyContinue | Where-Object { $null -ne $_.Path -and $oldDirs -contains (Split-Path $_.Path -Parent) })
foreach ($oldNode in $oldNodes) {
    $dir = Split-Path $oldNode.Path -Parent
    try {
        Stop-NodeProcesses @($oldNode)
    } catch {
        Fail "the older zyggy-node (pid $($oldNode.Id)) in $dir could not be stopped" "Stop-Process -Id $($oldNode.Id) -Force, then run again"
    }
    Step "stopped the older zyggy-node in $dir (it would hold port 4711)"
}
New-Item -ItemType Directory -Force -Path $programs | Out-Null
Copy-Item (Join-Path $Source 'zyggy.exe') $zyggyExe -Force
Copy-Item (Join-Path $Source 'zyggy-node.exe') $nodeExe -Force
Step "copied zyggy.exe and zyggy-node.exe to $programs"
if (($userPath -split ';') -notcontains $programs) {
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $programs).TrimStart(';'), 'User')
    Step "added $programs to the user PATH (new shells see it)"
} else {
    Step 'user PATH already holds the programs folder'
}
# Only the user PATH is written, only to drop a folder that holds nothing but zyggy* files; no file or folder is deleted.
$removed = @()
foreach ($dir in $oldDirs) {
    $others = @(Get-ChildItem -LiteralPath $dir -File -Force | Where-Object { $_.Name -notlike 'zyggy*' })
    if ($others.Count -eq 0) {
        try {
            $current = [Environment]::GetEnvironmentVariable('Path', 'User')
            $kept = @(($current -split ';') | Where-Object { $_.TrimEnd('\') -ne $dir })
            [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
            $removed += $dir
            Step "removed $dir from the user PATH (an older zyggy; its files are kept - delete the folder by hand when you no longer need it)"
        } catch {
            Step "warning: could not remove $dir from the user PATH: $($_.Exception.Message)"
        }
    } else {
        Step "warning: $dir holds an older zyggy next to other files and shadows the new one; move the zyggy files out or edit the user PATH by hand (rundll32 sysdm.cpl,EditEnvironmentVariables)"
    }
}
# A copy on the machine PATH shows up as a session entry that is not on the user PATH; the machine target is never read.
$userDirs = @(([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') | ForEach-Object { $_.TrimEnd('\') })
$otherDirs = @(($env:Path -split ';') | ForEach-Object { $_.TrimEnd('\') } | Where-Object { $_ -ne '' -and $_ -ne $programs -and $_ -notin $userDirs -and $_ -notin $removed -and (Test-Path -LiteralPath (Join-Path $_ 'zyggy.exe')) } | Sort-Object -Unique)
foreach ($dir in $otherDirs) {
    Step "warning: $dir holds an older zyggy and is not on the user PATH (the machine PATH needs whoever administers this machine)"
}
$env:Path = $programs + ';' + ((($env:Path -split ';') | Where-Object { $_ -ne '' -and $_.TrimEnd('\') -ne $programs -and $_.TrimEnd('\') -notin $removed }) -join ';')
Step "session PATH: $programs first"

# 3. the checkout's parent
$busParent = Split-Path $BusLocalPath -Parent
New-Item -ItemType Directory -Force -Path $busParent | Out-Null
Step "ensured $busParent"

# 4. node.json
# node.json-template-begin
$template = @'
{
  "tenant": "<tenant>",
  "machine": "<machine>",
  "signWith": "<signWith>",
  "bus": { "remote": "https://github.com/zyggy-org/zyggy-bus.git", "owner": "zyggy-org", "repo": "zyggy-bus", "branch": "main", "localPath": "<busLocalPath>" },
  "poll": { "activeSeconds": 10, "idleSeconds": 60, "safetyFetchMinutes": 15 },
  "workHours": { "timezone": "Europe/Brussels", "days": ["Mon", "Tue", "Wed", "Thu", "Fri"], "from": "08:00", "to": "19:00" },
  "devRoots": [],
  "maxConcurrentJobs": 2,
  "log": { "level": "Information" },
  "claude": { "path": "<userprofile>/.local/bin/claude.exe" },
  "secrets": { "store": "os" }
}
'@
# node.json-template-end
if (Test-Path $NodeJson) {
    Step "node.json: exists, left untouched ($NodeJson)"
} else {
    if ([string]::IsNullOrWhiteSpace($SignWith)) {
        Fail 'node.json is absent and -SignWith was not given' "run again with -SignWith $Tenant/<n> (the key number this machine signs with)"
    }
    $json = $template.Replace('<tenant>', $Tenant).Replace('<machine>', $Machine).Replace('<signWith>', $SignWith)
    $json = $json.Replace('<busLocalPath>', $BusLocalPath.Replace('\', '/')).Replace('<userprofile>', $userProfile.Replace('\', '/'))
    New-Item -ItemType Directory -Force -Path (Split-Path $NodeJson -Parent) | Out-Null
    [IO.File]::WriteAllText($NodeJson, $json, (New-Object Text.UTF8Encoding $false))
    Step "node.json: written ($NodeJson); edit workHours, maxConcurrentJobs or claude.path by hand if this laptop differs"
}

# 5. pre-flight: the conditions the node would otherwise refuse with its own lines
$gitVersion = (Invoke-Native 'git' @('--version')).Trim()
if ($LASTEXITCODE -ne 0) {
    Fail 'git is not on the PATH' 'install Git for Windows (Git Credential Manager is its default helper), then run again'
}
Step "git: $gitVersion"
if (-not (Test-Path (Join-Path $BusLocalPath '.git'))) {
    Fail "$BusLocalPath is not a git checkout" "git clone https://github.com/zyggy-org/zyggy-bus.git $BusLocalPath ; then git -C $BusLocalPath config core.autocrlf false"
}
if (-not (Test-Path (Join-Path $BusLocalPath "tenants\$Tenant"))) {
    Fail "$BusLocalPath holds no tenants\$Tenant folder" 'clone the bus repository of this tenant (see install-node.md); the node never creates the tenant folder'
}
Step "checkout: $BusLocalPath holds tenants\$Tenant"

# 6. zyggy setup, by its absolute path (an older zyggy may still be on PATH): one line per item on stdout, exit 0/2/3/4/6
$setupArgs = @('setup', '--config', $NodeJson, '--tenant', $Tenant, '--machine', $Machine, '--core-path', $CorePath)
if (-not [string]::IsNullOrWhiteSpace($SignWith)) {
    $setupArgs += @('--sign-with', $SignWith)
}
$setupOutput = Invoke-Native $zyggyExe $setupArgs
$setupExit = $LASTEXITCODE
Write-Host $setupOutput.TrimEnd()
if ($setupExit -eq 3) {
    Fail 'zyggy setup refused node.json (see the setup lines above)' 'restore node.json from its .bak-... copy or fix the key named, then run again'
}
if ($setupExit -eq 4) {
    Fail "the signing key is not in this user's store" "zyggy secret set hmac/<n> --hex  (paste the tenant's key at the hidden prompt; compare fingerprints with zyggy secret list; where the key comes from: rotate-hmac.md)"
}
if ($setupExit -ne 0 -and $setupExit -ne 6) {
    Fail "zyggy setup exited $setupExit" 'read the setup lines above'
}
$setupIncomplete = ($setupExit -eq 6)

# 7. pre-flight: claude.path, as zyggy setup left it
$config = Get-Content $NodeJson -Raw | ConvertFrom-Json
$claudePath = [string](Get-Optional (Get-Optional $config 'claude') 'path')
if ([string]::IsNullOrWhiteSpace($claudePath) -or -not (Test-Path $claudePath)) {
    Fail "claude.path ($claudePath) does not exist" "install Claude Code for this user (zyggy setup looks for claude.exe on the PATH or at $(Join-Path $userProfile '.local\bin\claude.exe')), then run again; or set claude.path in node.json to its absolute path"
}
Step "claude: $claudePath"

# 8. the per-user logon task (the contract table of spec 10)
$command = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"`$env:GCM_INTERACTIVE='never'; & '$programs\zyggy-node.exe' --config '$NodeJson'; exit `$LASTEXITCODE`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $command
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT30S'
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -Hidden -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
try {
    Register-ScheduledTask -TaskName 'zyggy-node' -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
} catch {
    if ($_.Exception.Message -match 'Access is denied') {
        Fail 'the system refuses a per-user scheduled task (a policy forbids them)' 'place a shortcut to the same powershell.exe command in shell:startup instead (see install-node.md); nothing here can or should elevate'
    }
    throw
}
Step "task: registered $taskName for $user (at logon, 30 s delay, hidden, restart on failure 3 x 1 min)"

# 9. start and report
Start-ScheduledTask -TaskName $taskName
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Seconds 1
    $state = (Get-ScheduledTask -TaskName $taskName).State
} while ($state -ne 'Running' -and (Get-Date) -lt $deadline)
Step "task: $state"
# Only the overview's first line (`node    <machine> (<tenant>) ...`): PowerShell 5.1 renders a merged stderr log line as
# `zyggy.exe : info: ...`, so filtering log prefixes is not enough (seen 2026-11-02).
$status = (Invoke-Native $zyggyExe @('status', '--config', $NodeJson)) -split "`r?`n" | Where-Object { $_ -match '^node\s' } | Select-Object -First 1
if ($null -eq $status) {
    $status = "no node line; run zyggy status --config $NodeJson"
}
Step "status: $status"
Step 'reopen: terminals, VS Code windows and Claude Code sessions opened before this install still have the old PATH and environment - close and reopen them (VS Code: all windows)'
if ($setupIncomplete) {
    Step 'incomplete - see the setup lines above'
    exit 1
}
