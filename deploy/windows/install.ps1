# install.ps1 — the Zyggy node on a Windows laptop, per user, without administrator rights (deliverable 10, founding spec §10).
#
# Shell under rule R1, with the reason: this script runs before any Zyggy binary is trusted, it drives the user's own
# Task Scheduler and user environment, and it cannot be unit-tested by the rules of this repository — its invariants are
# checked statically by tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs. Everything it does is per user: no step elevates
# and no step asks to. The owner is not an administrator on every laptop.
#
# What it does, in order (each step prints one line; the first failure stops with the remedy):
#   1. verifies SHA256SUMS for zyggy.exe and zyggy-node.exe when the file is next to them;
#   2. stops the logon task if it runs (the upgrade path), copies both binaries to %LOCALAPPDATA%\Programs\Zyggy, adds that folder to the user PATH once;
#   3. creates the parent of the bus checkout path;
#   4. writes node.json from the template below when absent (else leaves it untouched);
#   5. pre-flight: git, the checkout and its tenants\<tenant> folder, the signing key (zyggy secret list), claude.path;
#   6. registers or updates the per-user logon task zyggy-node (interactive logon type, limited run level, hidden, restart on failure);
#   7. starts the task and prints the first line of zyggy status.
# -Uninstall stops and unregisters the task only; the binaries, node.json, the checkout and the secrets stay.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tenant,
    [Parameter(Mandatory)] [string] $Machine,
    [string] $SignWith,
    [string] $BusLocalPath = 'C:\agents\bus',
    [string] $Source = $PSScriptRoot,
    [string] $NodeJson = (Join-Path $env:LOCALAPPDATA 'zyggy\node.json'),
    [switch] $Uninstall
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$taskName = 'zyggy-node'
$programs = Join-Path $env:LOCALAPPDATA 'Programs\Zyggy'
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

function Get-TaskOrNull {
    return Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
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
            Stop-ScheduledTask -TaskName $taskName
            Step "uninstall: stopped $taskName"
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

# 2. the binaries, per user
$task = Get-TaskOrNull
if ($null -ne $task -and $task.State -eq 'Running') {
    Stop-ScheduledTask -TaskName $taskName
    Step "stopped $taskName for the upgrade"
}
New-Item -ItemType Directory -Force -Path $programs | Out-Null
Copy-Item (Join-Path $Source 'zyggy.exe') $zyggyExe -Force
Copy-Item (Join-Path $Source 'zyggy-node.exe') $nodeExe -Force
Step "copied zyggy.exe and zyggy-node.exe to $programs"
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) { $userPath = '' }
if (($userPath -split ';') -notcontains $programs) {
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $programs).TrimStart(';'), 'User')
    Step "added $programs to the user PATH (new shells see it)"
} else {
    Step 'user PATH already holds the programs folder'
}

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
    $json = $json.Replace('<busLocalPath>', $BusLocalPath.Replace('\', '/')).Replace('<userprofile>', $env:USERPROFILE.Replace('\', '/'))
    New-Item -ItemType Directory -Force -Path (Split-Path $NodeJson -Parent) | Out-Null
    [IO.File]::WriteAllText($NodeJson, $json, (New-Object Text.UTF8Encoding $false))
    Step "node.json: written ($NodeJson); edit workHours, maxConcurrentJobs or claude.path by hand if this laptop differs"
}

# 5. pre-flight: the conditions the node would otherwise refuse with its own lines
$gitVersion = (& git --version 2>&1 | Out-String).Trim()
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
$secretList = (& $zyggyExe secret list --config $NodeJson 2>&1 | Out-String)
if ($secretList -match 'file secret store selected on Windows') {
    Step 'secrets: the file store is selected (spike); rotate-hmac.md step 0 retires the spike file store'
}
if ($secretList -notmatch '(?m)^hmac/\d+\s.*\ssigns') {
    $config = Get-Content $NodeJson -Raw | ConvertFrom-Json
    $keyNumber = ($config.signWith -split '/')[-1]
    Fail "the signing key $($config.signWith) is not in this user's store" "zyggy secret set hmac/$keyNumber --hex  (paste the tenant's key at the hidden prompt; compare fingerprints with zyggy secret list)"
}
Step 'secrets: the signing key is present (zyggy secret list shows it as signs)'
$config = Get-Content $NodeJson -Raw | ConvertFrom-Json
$claudePath = $config.claude.path
if ([string]::IsNullOrWhiteSpace($claudePath) -or -not (Test-Path $claudePath)) {
    Fail "claude.path ($claudePath) does not exist" 'install Claude Code for this user and set claude.path in node.json to its absolute path'
}
Step "claude: $claudePath"

# 6. the per-user logon task (the contract table of spec 10)
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

# 7. start and report
Start-ScheduledTask -TaskName $taskName
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Seconds 1
    $state = (Get-ScheduledTask -TaskName $taskName).State
} while ($state -ne 'Running' -and (Get-Date) -lt $deadline)
Step "task: $state"
$status = (& $zyggyExe status --config $NodeJson 2>&1 | Out-String) -split "`n" | Select-Object -First 1
Step "status: $status"
