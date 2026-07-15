[CmdletBinding()]
param(
    [ValidateSet('static', 'task-plan', 'state-security', 'runtime-acl', 'native-takeown', 'identity-mismatch', 'identity-binding', 'task-neutralization', 'late-failure-neutralization', 'critical-state-revalidation', 'installer-mutex', 'manifest-install', 'corrupt-manager', 'corrupt-installed-manager', 'stale-cleanup', 'stop-race', 'stop-timeout', 'transactional-install', 'transaction-hardening', 'reinstall-idempotent', 'all')]
    [string]$Scenario = 'all'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'install_service_host.ps1'
$content = Get-Content -Raw -LiteralPath $scriptPath
$deployContent = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'deploy_servicehost.ps1')
if ($content -notmatch '(?s)\bparam\s*\([\s\S]*\[switch\]\s*\$AsLibrary') {
    throw 'Installer does not provide the required -AsLibrary safety boundary.'
}
. $scriptPath -AsLibrary

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) { throw "$Because. Expected [$Expected], got [$Actual]." }
}

function Assert-True([bool]$Condition, [string]$Because) {
    if (-not $Condition) { throw $Because }
}

function Assert-ThrowsMessage([scriptblock]$Action, [string]$ExpectedMessage, [string]$Because) {
    try { & $Action }
    catch { Assert-Equal $ExpectedMessage $_.Exception.Message $Because; return }
    throw $Because
}

function Assert-Contains([string]$Pattern, [string]$Message) {
    if ($content -notmatch $Pattern) { throw $Message }
}

function Assert-NotContains([string]$Pattern, [string]$Message) {
    if ($content -match $Pattern) { throw $Message }
}

function New-InstallerFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('service-host-installer-test-' + [guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'share'
    $install = Join-Path $root 'install'
    $statePath = Join-Path $root 'state'
    $desktop = Join-Path $root 'desktop'
    New-Item -ItemType Directory -Force -Path $source, $install, $statePath, $desktop, (Join-Path $source 'nested') | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'QuickBooksServiceHost.exe'), 'host')
    [IO.File]::WriteAllText((Join-Path $source 'QuickBooksConnectorCli.exe'), 'cli')
    [IO.File]::WriteAllText((Join-Path $source 'nested\runtime.dll'), 'runtime')
    [IO.File]::WriteAllText((Join-Path $source 'unlisted.dll'), 'excluded')
    [IO.File]::WriteAllText((Join-Path $source 'service_host_manager.ps1'), '# manager')
    [IO.File]::WriteAllText((Join-Path $source 'bridge.settings.psd1'), "@{`nQB_BRIDGE_TOKEN='fixture-token'`nQB_BRIDGE_ORIGIN='http://fixture:8742'`nQB_BRIDGE_PORT='8788'`n}")
    $entries = foreach ($relative in @('QuickBooksServiceHost.exe', 'QuickBooksConnectorCli.exe', 'nested/runtime.dll')) {
        $path = Join-Path $source $relative
        [pscustomobject]@{
            path = $relative
            length = [long](Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
    }
    $manager = Join-Path $source 'service_host_manager.ps1'
    [pscustomobject]@{
        schema_version = 1
        release_id = 'release-install'
        published_at_utc = '2026-07-15T10:00:00Z'
        files = @($entries)
        manager = [pscustomobject]@{
            path = 'service_host_manager.ps1'
            length = [long](Get-Item -LiteralPath $manager).Length
            sha256 = (Get-FileHash -LiteralPath $manager -Algorithm SHA256).Hash
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $source 'release.manifest.json')
    [pscustomobject]@{
        Root = $root
        Source = $source
        Install = $install
        StatePath = $statePath
        Desktop = $desktop
        HostPath = Join-Path $install 'QuickBooksServiceHost.exe'
        ManagerPath = Join-Path $statePath 'Manager\service_host_manager.ps1'
        InstalledManifestPath = Join-Path $statePath 'release.manifest.json'
        PreviousPath = Join-Path $statePath 'Previous'
    }
}

function Set-InstalledReleaseFixture($Fixture) {
    New-Item -ItemType Directory -Force -Path $Fixture.Install, (Join-Path $Fixture.Install 'nested'), (Split-Path -Parent $Fixture.ManagerPath) | Out-Null
    [IO.File]::WriteAllText($Fixture.HostPath, 'old-host')
    [IO.File]::WriteAllText((Join-Path $Fixture.Install 'QuickBooksConnectorCli.exe'), 'old-cli')
    [IO.File]::WriteAllText((Join-Path $Fixture.Install 'nested\runtime.dll'), 'old-runtime')
    [IO.File]::WriteAllText($Fixture.ManagerPath, '# old manager')
    [IO.File]::WriteAllText($Fixture.InstalledManifestPath, '{"schema_version":1,"release_id":"release-old"}')
}

function Assert-OldReleaseRestored($Fixture, [string]$Because) {
    Assert-Equal 'old-host' ([IO.File]::ReadAllText($Fixture.HostPath)) "$Because host"
    Assert-Equal 'old-cli' ([IO.File]::ReadAllText((Join-Path $Fixture.Install 'QuickBooksConnectorCli.exe'))) "$Because connector"
    Assert-Equal 'old-runtime' ([IO.File]::ReadAllText((Join-Path $Fixture.Install 'nested\runtime.dll'))) "$Because runtime"
    Assert-Equal '# old manager' ([IO.File]::ReadAllText($Fixture.ManagerPath)) "$Because manager"
    Assert-Equal 'release-old' ((Get-Content -Raw -LiteralPath $Fixture.InstalledManifestPath | ConvertFrom-Json).release_id) "$Because manifest"
}

function Set-RetainedPreviousFixture($Fixture) {
    $payload = Join-Path $Fixture.PreviousPath 'Payload'
    $manager = Join-Path $Fixture.PreviousPath 'Manager'
    New-Item -ItemType Directory -Force -Path $payload, $manager | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'retained.txt'), 'retained-payload')
    [IO.File]::WriteAllText((Join-Path $manager 'retained-manager.txt'), 'retained-manager')
    [IO.File]::WriteAllText((Join-Path $Fixture.PreviousPath 'retained-manifest.txt'), 'retained-manifest')
}

function Assert-RetainedPreviousFixture($Fixture, [string]$Because) {
    Assert-Equal 'retained-payload' ([IO.File]::ReadAllText((Join-Path $Fixture.PreviousPath 'Payload\retained.txt'))) "$Because payload"
    Assert-Equal 'retained-manager' ([IO.File]::ReadAllText((Join-Path $Fixture.PreviousPath 'Manager\retained-manager.txt'))) "$Because manager"
    Assert-Equal 'retained-manifest' ([IO.File]::ReadAllText((Join-Path $Fixture.PreviousPath 'retained-manifest.txt'))) "$Because manifest"
}

function Remove-InstallerFixture($Fixture) {
    Remove-Item -LiteralPath $Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
}

function New-InjectedActions {
    param($Fixture)
    $state = [pscustomobject]@{
        CopyCalls = 0
        MoveCalls = 0
        RemoveCalls = 0
        RegisterCalls = 0
        StartCalls = 0
        DirectStartCalls = 0
        StopCalls = 0
        WaitCalls = 0
        NextId = 100
        Processes = @()
        Tasks = @{}
        Environment = @{}
        Shortcut = $null
        StopRequested = $false
        HostExited = $true
        ExitPollsRemaining = 0
        NeverExit = $false
        RequireExitBeforeCopy = $false
        SecurityPasses = 0
        RuntimeSecurityPasses = 0
        Locked = $false
        MutexName = ''
        MutexWaitMilliseconds = 0
        Events = @()
        NeutralizeCalls = 0
    }
    [pscustomobject]@{
        State = $state
        CurrentIdentity = { [pscustomobject]@{ Name = 'DOMAIN\estimator'; Sid = 'S-1-5-21-1000' } }
        ResolveUserSid = { param($name) 'S-1-5-21-1000' }
        TestAdministrator = { $true }
        ReadManifest = { param($path) Get-Content -Raw -LiteralPath $path | ConvertFrom-Json }
        EnsureDirectory = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null }
        CopyFile = {
            param($source, $destination)
            if ($state.RequireExitBeforeCopy -and -not $state.HostExited -and
                $destination.StartsWith($Fixture.Install, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'runtime copy happened before host exit'
            }
            $state.CopyCalls++
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }.GetNewClosure()
        RemovePath = {
            param($path, [bool]$recurse)
            if ($state.RequireExitBeforeCopy -and -not $state.HostExited -and
                $path.StartsWith($Fixture.Install, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'runtime cleanup happened before host exit'
            }
            $state.RemoveCalls++
            Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction SilentlyContinue
        }.GetNewClosure()
        MovePath = {
            param($source, $destination)
            $state.MoveCalls++
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            Move-Item -LiteralPath $source -Destination $destination -Force
        }.GetNewClosure()
        SetEnvironmentVariable = { param($name, $value) $state.Environment[$name] = [string]$value }.GetNewClosure()
        CreateShortcut = {
            param($path, $target, $workingDirectory)
            $state.Shortcut = [pscustomobject]@{ Path = $path; TargetPath = $target; WorkingDirectory = $workingDirectory }
        }.GetNewClosure()
        RegisterTask = { param($plan) $state.RegisterCalls++; $state.Tasks[$plan.TaskName] = $plan }.GetNewClosure()
        StartTask = {
            param($taskName)
            $state.StartCalls++
            $state.StopRequested = $false
            if (-not @($state.Processes | Where-Object { $_.Path -ieq $Fixture.HostPath }).Count) {
                $state.NextId++
                $state.Processes = @($state.Processes) + @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = $state.NextId; Path = $Fixture.HostPath })
            }
        }.GetNewClosure()
        StartInstalledHost = {
            param($hostPath, $workingDirectory)
            $state.DirectStartCalls++
            $state.StopRequested = $false
            if (-not @($state.Processes | Where-Object { $_.Path -ieq $hostPath }).Count) {
                $state.NextId++
                $state.Processes = @($state.Processes) + @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = $state.NextId; Path = $hostPath })
            }
        }.GetNewClosure()
        GetTask = { param($taskName) $state.Tasks[$taskName] }.GetNewClosure()
        GetProcesses = {
            if ($state.StopRequested -and @($state.Processes).Count -gt 0) {
                if (-not $state.NeverExit) {
                    if ($state.ExitPollsRemaining -gt 0) { $state.ExitPollsRemaining-- }
                    else { $state.Processes = @(); $state.HostExited = $true }
                }
            }
            @($state.Processes)
        }.GetNewClosure()
        StopProcess = { param($process) $state.StopCalls++; $state.StopRequested = $true }.GetNewClosure()
        Wait = { $state.WaitCalls++ }.GetNewClosure()
        ProtectStateTree = { param($path) $state.SecurityPasses++ }.GetNewClosure()
        ProtectRuntimeTree = { param($path) $state.RuntimeSecurityPasses++ }.GetNewClosure()
        GetVolumeIdentity = { param($path) 'fixture-volume-id' }
        Mutex = {
            param($name, $waitMilliseconds, $action)
            $state.MutexName = $name
            $state.MutexWaitMilliseconds = $waitMilliseconds
            $state.Events += "mutex:$name"
            $state.Locked = $true
            try { & $action }
            finally { $state.Locked = $false; $state.Events += 'release' }
        }.GetNewClosure()
        NeutralizeTask = { param($taskName) $state.NeutralizeCalls++; $state.Tasks.Remove($taskName) }.GetNewClosure()
    }
}

function Invoke-FixtureInstall {
    param($Fixture, $Actions, [int]$StopWaitAttempts = 4)
    $arguments = @{
        SourcePath = $Fixture.Source
        InstallPath = $Fixture.Install
        StatePath = $Fixture.StatePath
        PublicDesktopPath = $Fixture.Desktop
        InteractiveUser = 'DOMAIN\estimator'
        InteractiveSid = 'S-1-5-21-1000'
        GetCurrentIdentityAction = $Actions.CurrentIdentity
        ResolveInteractiveUserSidAction = $Actions.ResolveUserSid
        TestAdministratorAction = $Actions.TestAdministrator
        ReadManifestAction = $Actions.ReadManifest
        EnsureDirectoryAction = $Actions.EnsureDirectory
        CopyFileAction = $Actions.CopyFile
        SetEnvironmentVariableAction = $Actions.SetEnvironmentVariable
        CreateShortcutAction = $Actions.CreateShortcut
        RegisterTaskAction = $Actions.RegisterTask
        StartTaskAction = $Actions.StartTask
        GetTaskAction = $Actions.GetTask
        GetProcessesAction = $Actions.GetProcesses
        StopProcessAction = $Actions.StopProcess
        WaitAction = $Actions.Wait
    }
    $parameters = (Get-Command Invoke-ServiceHostInstall).Parameters
    if ($parameters.ContainsKey('RemovePathAction')) { $arguments.RemovePathAction = $Actions.RemovePath }
    if ($parameters.ContainsKey('MovePathAction')) { $arguments.MovePathAction = $Actions.MovePath }
    if ($parameters.ContainsKey('StopWaitAttempts')) { $arguments.StopWaitAttempts = $StopWaitAttempts }
    if ($parameters.ContainsKey('ProtectStateTreeAction')) { $arguments.ProtectStateTreeAction = $Actions.ProtectStateTree }
    if ($parameters.ContainsKey('ProtectRuntimeTreeAction')) { $arguments.ProtectRuntimeTreeAction = $Actions.ProtectRuntimeTree }
    if ($parameters.ContainsKey('GetVolumeIdentityAction')) { $arguments.GetVolumeIdentityAction = $Actions.GetVolumeIdentity }
    if ($parameters.ContainsKey('MutexAction')) { $arguments.MutexAction = $Actions.Mutex }
    if ($parameters.ContainsKey('NeutralizeTaskAction')) { $arguments.NeutralizeTaskAction = $Actions.NeutralizeTask }
    if ($parameters.ContainsKey('StartInstalledHostAction')) { $arguments.StartInstalledHostAction = $Actions.StartInstalledHost }
    Invoke-ServiceHostInstall @arguments | Out-Null
}

function Run-Scenario {
    param([string]$Name)
    switch ($Name) {
        'static' {
            Assert-Contains '\$ErrorActionPreference\s*=\s*[''"]Stop[''"]' 'stop on failure'
            Assert-Contains 'Start-Process[\s\S]*-Verb\s+RunAs' 'elevation'
            Assert-Contains 'New-ScheduledTaskAction' 'task action'
            Assert-Contains 'New-ScheduledTaskTrigger[\s\S]*-AtLogOn' 'logon trigger'
            Assert-Contains 'New-ScheduledTaskTrigger[\s\S]*-Daily' 'daily trigger'
            Assert-Contains 'New-ScheduledTaskPrincipal[\s\S]*Interactive[\s\S]*Highest' 'principal'
            Assert-Contains 'New-ScheduledTaskSettingsSet[\s\S]*StartWhenAvailable[\s\S]*MultipleInstances\s+IgnoreNew' 'settings'
            Assert-Contains 'Register-ScheduledTask[\s\S]*-Force' 'force register'
            Assert-NotContains 'Remove-Item[^\r\n]*bridge\.settings\.psd1' 'preserve settings'
            Assert-NotContains 'RemovePathAction[^\r\n]*SilentlyContinue' 'runtime cleanup failures must stop installation'
            Assert-True ($deployContent -match 'QuickBooksConnectorCli\\bin\\Release' -and $deployContent -match 'QuickBooksServiceHost\\bin\\Release') 'deploy packages outputs'
            Assert-True ($deployContent.IndexOf('Copy-SourceIntoPayload -SourcePath $HostSourcePath') -ge $deployContent.IndexOf('Copy-SourceIntoPayload -SourcePath $ConnectorSourcePath')) 'host wins deploy collisions'
        }
        'task-plan' {
            $plan = New-ServiceHostTaskPlan -InteractiveUser 'DOMAIN\estimator' -ManagerPath 'C:\ProgramData\QuickBooksServiceHost\Manager\service_host_manager.ps1'
            Assert-Equal 'QuickBooksServiceHost Auto Start and Update' $plan.TaskName 'task name'
            Assert-Equal 'DOMAIN\estimator' $plan.UserId 'interactive principal'
            Assert-Equal 'Highest' $plan.RunLevel 'highest'
            Assert-Equal 'Interactive' $plan.LogonType 'logon type'
            Assert-Equal '06:00:00' $plan.DailyAt 'daily'
            Assert-True $plan.AtLogOn 'logon'
            Assert-True $plan.StartWhenAvailable 'late start'
            Assert-True (-not $plan.WakeToRun) 'no wake'
            Assert-Equal 'IgnoreNew' $plan.MultipleInstances 'no overlap'
            Assert-Equal 'powershell.exe' $plan.Execute 'shell'
            Assert-Equal '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuickBooksServiceHost\Manager\service_host_manager.ps1"' $plan.Arguments 'arguments'
        }
        'state-security' {
            $administratorSid = 'S-1-5-32-544'
            $systemSid = 'S-1-5-18'
            $usersSid = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
            $insecureAcl = New-Object Security.AccessControl.DirectorySecurity
            $insecureAcl.SetOwner($usersSid)
            $insecureAcl.SetAccessRuleProtection($true, $false)
            $usersRule = New-Object Security.AccessControl.FileSystemAccessRule(
                $usersSid,
                [Security.AccessControl.FileSystemRights]::Modify,
                ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
            $insecureAcl.AddAccessRule($usersRule)
            Assert-True (-not (Test-ServiceHostSecureAcl -Acl $insecureAcl -IsContainer $true)) 'writable Users ACL and owner mismatch are rejected'

            $root = 'C:\ProgramData\QuickBooksServiceHost-Test'
            $manager = Join-Path $root 'Manager'
            $logs = Join-Path $root 'Logs'
            $hostile = Join-Path $root 'Hostile'
            $hostileFile = Join-Path $hostile 'state.json'
            $items = @{}
            foreach ($entry in @(
                [pscustomobject]@{ FullName = $root; PSIsContainer = $true; Attributes = [IO.FileAttributes]::Directory; Parent = '' },
                [pscustomobject]@{ FullName = $hostile; PSIsContainer = $true; Attributes = [IO.FileAttributes]::Directory; Parent = $root },
                [pscustomobject]@{ FullName = $hostileFile; PSIsContainer = $false; Attributes = [IO.FileAttributes]::Normal; Parent = $hostile }
            )) { $items[$entry.FullName] = $entry }
            $acls = @{ $root = $insecureAcl; $hostile = $insecureAcl; $hostileFile = $insecureAcl }
            $ensureDirectory = {
                param($path)
                if (-not $items.ContainsKey($path)) {
                    $items[$path] = [pscustomobject]@{ FullName = $path; PSIsContainer = $true; Attributes = [IO.FileAttributes]::Directory; Parent = Split-Path -Parent $path }
                    $acls[$path] = $insecureAcl
                }
            }.GetNewClosure()
            $getItem = { param($path) $items[$path] }.GetNewClosure()
            $getChildren = { param($path) @($items.Values | Where-Object { $_.Parent -eq $path }) }.GetNewClosure()
            $ownersTaken = @{}
            $getAcl = {
                param($path)
                if (-not $ownersTaken.ContainsKey($path)) { throw [UnauthorizedAccessException]::new('READ_CONTROL denied until ownership takeover') }
                $acls[$path]
            }.GetNewClosure()
            $setOwner = { param($path, $ownerSid) $ownersTaken[$path] = $true; $acls[$path].SetOwner($ownerSid) }.GetNewClosure()
            $setAcl = { param($path, $acl) if (-not $ownersTaken.ContainsKey($path)) { throw 'ACL set before ownership repair' }; $acls[$path] = $acl }.GetNewClosure()

            Protect-ServiceHostStateTree -StatePath $root -EnsureDirectoryAction $ensureDirectory `
                -GetItemAction $getItem -GetChildrenAction $getChildren -GetAclAction $getAcl `
                -SetOwnerAction $setOwner -SetAclAction $setAcl | Out-Null
            foreach ($path in @($root, $manager, $logs, $hostile, $hostileFile)) {
                Assert-True (Test-ServiceHostSecureAcl -Acl $acls[$path] -IsContainer ([bool]$items[$path].PSIsContainer)) "secure ACL repaired for $path"
                Assert-Equal $administratorSid $acls[$path].GetOwner([Security.Principal.SecurityIdentifier]).Value "administrator owner for $path"
                $sids = @($acls[$path].GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) | ForEach-Object { $_.IdentityReference.Value } | Sort-Object)
                Assert-Equal "$systemSid|$administratorSid" ($sids -join '|') "only SYSTEM and Administrators allowed for $path"
            }

            $link = Join-Path $root 'Linked'
            $items[$link] = [pscustomobject]@{ FullName = $link; PSIsContainer = $true; Attributes = ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint); Parent = $root }
            $acls[$link] = $insecureAcl
            $linkEnumerations = [pscustomobject]@{ Count = 0 }
            $getChildrenWithLinkGuard = {
                param($path)
                if ($path -eq $link) { $linkEnumerations.Count++; throw 'followed reparse point' }
                @($items.Values | Where-Object { $_.Parent -eq $path })
            }.GetNewClosure()
            Assert-ThrowsMessage {
                Protect-ServiceHostStateTree -StatePath $root -EnsureDirectoryAction $ensureDirectory `
                    -GetItemAction $getItem -GetChildrenAction $getChildrenWithLinkGuard -GetAclAction $getAcl `
                    -SetOwnerAction $setOwner -SetAclAction $setAcl | Out-Null
            } "Reparse points are not allowed in the service host state tree: $link" 'reparse point rejected'
            Assert-Equal 0 $linkEnumerations.Count 'reparse directory is never traversed'

            $items.Remove($link)
            $acls.Remove($link)
            $swap = Join-Path $root 'SwapAfterOwner'
            $items[$swap] = [pscustomobject]@{ FullName = $swap; PSIsContainer = $true; Attributes = [IO.FileAttributes]::Directory; Parent = $root }
            $acls[$swap] = $insecureAcl
            $swapAclCalls = [pscustomobject]@{ Count = 0 }
            $swapOwner = {
                param($path, $ownerSid)
                $ownersTaken[$path] = $true
                $acls[$path].SetOwner($ownerSid)
                if ($path -eq $swap) { $items[$path].Attributes = [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint }
            }.GetNewClosure()
            $swapSetAcl = { param($path, $acl) if ($path -eq $swap) { $swapAclCalls.Count++ }; $acls[$path] = $acl }.GetNewClosure()
            Assert-ThrowsMessage {
                Protect-ServiceHostStateTree -StatePath $root -EnsureDirectoryAction $ensureDirectory `
                    -GetItemAction $getItem -GetChildrenAction $getChildren -GetAclAction $getAcl `
                    -SetOwnerAction $swapOwner -SetAclAction $swapSetAcl | Out-Null
            } "Reparse points are not allowed in the service host state tree: $swap" 'reparse replacement after owner step rejected'
            Assert-Equal 0 $swapAclCalls.Count 'canonical ACL is not applied through a replaced reparse point'

            $takeown = [pscustomobject]@{ FilePath = ''; Arguments = @() }
            Set-ServiceHostPathOwner -Path 'C:\ProgramData\Hostile' `
                -OwnerSid (New-Object Security.Principal.SecurityIdentifier $administratorSid) `
                -InvokeTakeOwnershipAction {
                    param($filePath, $arguments)
                    $takeown.FilePath = $filePath
                    $takeown.Arguments = @($arguments)
                    [pscustomobject]@{ ExitCode = 0; Output = 'SUCCESS' }
                }.GetNewClosure()
            Assert-True ($takeown.FilePath -match '(?i)takeown\.exe$') 'native ownership boundary invokes takeown.exe'
            Assert-Equal '/F|C:\ProgramData\Hostile|/A' ($takeown.Arguments -join '|') 'takeown uses exact nonrecursive Administrators-owner arguments'
            Assert-ThrowsMessage {
                Set-ServiceHostPathOwner -Path 'C:\ProgramData\Denied' `
                    -OwnerSid (New-Object Security.Principal.SecurityIdentifier $administratorSid) `
                    -InvokeTakeOwnershipAction { param($filePath, $arguments) [pscustomobject]@{ ExitCode = 5; Output = 'Access denied' } }
            } 'Failed to take Administrators ownership of service host state path: C:\ProgramData\Denied (exit 5).' 'takeown nonzero exit fails clearly'

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $events = New-Object Collections.Generic.List[string]
                $actions.ProtectStateTree = { param($path) $actions.State.SecurityPasses++; $events.Add("secure:$($actions.State.SecurityPasses)") }.GetNewClosure()
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    if ($destination.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase) -and $actions.State.SecurityPasses -lt 1) {
                        throw 'ProgramData copy occurred before security initialization'
                    }
                    & $originalCopy $source $destination
                }.GetNewClosure()
                $actions.RegisterTask = {
                    param($plan)
                    Assert-True ($actions.State.SecurityPasses -ge 7) 'all critical state boundaries re-verified before task registration'
                    $events.Add('register')
                    $actions.State.RegisterCalls++
                    $actions.State.Tasks[$plan.TaskName] = $plan
                }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 'register' $events[$events.Count - 1] 'registration is the final locked event'
                Assert-True ($events[$events.Count - 2] -like 'secure:*') 'fresh security validation immediately guards task registration'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'runtime-acl' {
            $administratorSid = 'S-1-5-32-544'
            $systemSid = 'S-1-5-18'
            $usersSid = 'S-1-5-32-545'
            foreach ($isContainer in @($true, $false)) {
                $acl = New-ServiceHostRuntimeAcl -IsContainer $isContainer
                Assert-True (Test-ServiceHostRuntimeAcl -Acl $acl -IsContainer $isContainer) "canonical runtime ACL validates (container=$isContainer)"
                Assert-Equal $administratorSid $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value 'runtime owner is Administrators'
                Assert-True $acl.AreAccessRulesProtected 'runtime ACL inheritance is protected'
                $rules = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
                Assert-Equal 3 $rules.Count 'runtime ACL has exactly three explicit allow rules'
                foreach ($privilegedSid in @($administratorSid, $systemSid)) {
                    $rule = @($rules | Where-Object { $_.IdentityReference.Value -eq $privilegedSid })
                    Assert-Equal 1 $rule.Count "one privileged runtime rule for $privilegedSid"
                    Assert-Equal ([Security.AccessControl.FileSystemRights]::FullControl) $rule[0].FileSystemRights "privileged runtime rule is FullControl for $privilegedSid"
                }
                $usersRule = @($rules | Where-Object { $_.IdentityReference.Value -eq $usersSid })
                Assert-Equal 1 $usersRule.Count 'one BUILTIN Users runtime rule'
                $expectedUsersRights = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize
                Assert-Equal $expectedUsersRights $usersRule[0].FileSystemRights 'BUILTIN Users receive ReadAndExecute plus the .NET Synchronize bit only'
                Assert-True (($usersRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'BUILTIN Users do not receive Write'
                Assert-True ($usersRule[0].FileSystemRights -ne [Security.AccessControl.FileSystemRights]::Modify) 'BUILTIN Users do not receive Modify'
            }

            $root = Join-Path ([IO.Path]::GetTempPath()) ("service-host-runtime-acl-" + [guid]::NewGuid().ToString('N'))
            $nested = Join-Path $root 'nested'
            $leaf = Join-Path $nested 'host.exe'
            New-Item -ItemType Directory -Path $nested -Force | Out-Null
            Set-Content -LiteralPath $leaf -Value 'host'
            try {
                $applied = @{}
                Protect-ServiceHostRuntimeTree -InstallPath $root `
                    -GetItemAction { param($path) Get-Item -LiteralPath $path -Force } `
                    -GetChildrenAction { param($path) @(Get-ChildItem -LiteralPath $path -Force) } `
                    -SetAclAction { param($path, $acl) $applied[$path] = $acl }.GetNewClosure() `
                    -GetAclAction { param($path) $applied[$path] }.GetNewClosure() | Out-Null
                foreach ($path in @($root, $nested, $leaf)) {
                    Assert-True $applied.ContainsKey($path) "runtime ACL applied to $path"
                    Assert-True (Test-ServiceHostRuntimeAcl -Acl $applied[$path] -IsContainer ([bool](Get-Item -LiteralPath $path).PSIsContainer)) "runtime ACL verified for $path"
                }
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 1 $actions.State.RuntimeSecurityPasses 'successful promotion applies and verifies the runtime ACL once'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'native-takeown' {
            $originalPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Stop'
                $simulated = Invoke-ServiceHostNativeCommand -FilePath 'simulated-takeown.exe' -Arguments @('/F', 'C:\ProgramData\Denied', '/A') `
                    -InvokeNativeAction {
                        param($filePath, $arguments)
                        Write-Error 'simulated PS5.1 native stderr record'
                        [pscustomobject]@{ ExitCode = 5; Output = 'Access is denied.' }
                    }
                Assert-Equal 5 $simulated.ExitCode 'PS5.1-style native stderr still returns an explicit exit code'
                Assert-True ($simulated.Output -match 'Access is denied') 'native runner returns sanitized diagnostic output'
                Assert-Equal 'Stop' $ErrorActionPreference 'native runner restores caller error policy'

                $realProbe = Invoke-ServiceHostNativeCommand -FilePath $env:ComSpec `
                    -Arguments @('/d', '/c', 'echo service-host-native-probe 1>&2 & exit /b 7')
                Assert-Equal 7 $realProbe.ExitCode 'safe real native probe captures nonzero exit'
                Assert-True ($realProbe.Output -match 'service-host-native-probe') 'safe real native probe captures stderr'
                Assert-Equal 'Stop' $ErrorActionPreference 'real native probe restores caller error policy'
            }
            finally { $ErrorActionPreference = $originalPreference }
        }
        'identity-mismatch' {
            $fixture = New-InstallerFixture
            try {
                $state = [pscustomobject]@{ Copies = 0; Registers = 0; Starts = 0 }
                Assert-ThrowsMessage {
                    Invoke-ServiceHostInstall -SourcePath $fixture.Source -InstallPath $fixture.Install -StatePath $fixture.StatePath `
                        -PublicDesktopPath $fixture.Desktop -InteractiveUser 'user' -InteractiveSid 'expected' `
                        -GetCurrentIdentityAction { [pscustomobject]@{ Name = 'other'; Sid = 'wrong' } } `
                        -TestAdministratorAction { $true } -ReadManifestAction { throw 'read too early' } `
                        -EnsureDirectoryAction { throw 'directory too early' } -CopyFileAction { $state.Copies++ } `
                        -RemovePathAction { throw 'remove too early' } -SetEnvironmentVariableAction { throw 'env too early' } `
                        -CreateShortcutAction { throw 'shortcut too early' } -RegisterTaskAction { $state.Registers++ } `
                        -StartTaskAction { $state.Starts++ } -GetTaskAction { $null } -GetProcessesAction { @() } `
                        -StopProcessAction { throw 'stop too early' } -WaitAction { throw 'wait too early' }
                } 'Installer elevation must use the same local-administrator account that runs QuickBooks.' 'identity mismatch'
                Assert-Equal 0 $state.Copies 'no copies'
                Assert-Equal 0 $state.Registers 'no registration'
                Assert-Equal 0 $state.Starts 'no startup'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'identity-binding' {
            Assert-ElevatedIdentityMatches -InteractiveUser 'DOMAIN\estimator' -InteractiveSid 'S-1-5-21-1000' `
                -GetCurrentIdentityAction { [pscustomobject]@{ Name = 'DOMAIN\estimator'; Sid = 'S-1-5-21-1000' } } `
                -ResolveInteractiveUserSidAction { param($name) 'S-1-5-21-1000' } -TestAdministratorAction { $true }

            $fixture = New-InstallerFixture
            try {
                $state = [pscustomobject]@{ Copies = 0; Registers = 0; Starts = 0 }
                Assert-ThrowsMessage {
                    Invoke-ServiceHostInstall -SourcePath $fixture.Source -InstallPath $fixture.Install -StatePath $fixture.StatePath `
                        -PublicDesktopPath $fixture.Desktop -InteractiveUser 'DOMAIN\other' -InteractiveSid 'S-1-5-21-1000' `
                        -GetCurrentIdentityAction { [pscustomobject]@{ Name = 'DOMAIN\estimator'; Sid = 'S-1-5-21-1000' } } `
                        -ResolveInteractiveUserSidAction { param($name) 'S-1-5-21-2000' } -TestAdministratorAction { $true } `
                        -ReadManifestAction { throw 'manifest read before user binding' } -EnsureDirectoryAction { throw 'directory mutation before user binding' } `
                        -CopyFileAction { $state.Copies++ } -RemovePathAction { throw 'remove before user binding' } `
                        -SetEnvironmentVariableAction { throw 'environment mutation before user binding' } `
                        -CreateShortcutAction { throw 'shortcut mutation before user binding' } -RegisterTaskAction { $state.Registers++ } `
                        -StartTaskAction { $state.Starts++ } -GetTaskAction { $null } -GetProcessesAction { @() } `
                        -StopProcessAction { throw 'process mutation before user binding' } -WaitAction { throw 'wait before user binding' } `
                        -ProtectStateTreeAction { throw 'state mutation before user binding' }
                } 'InteractiveUser must resolve to the verified InteractiveSid.' 'wrong username cannot select another task principal'
                Assert-Equal 0 $state.Copies 'no copies before username binding'
                Assert-Equal 0 $state.Registers 'no registration before username binding'
                Assert-Equal 0 $state.Starts 'no startup before username binding'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'task-neutralization' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $taskName = 'QuickBooksServiceHost Auto Start and Update'
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName; Hostile = $true }
                $actions.NeutralizeTask = {
                    param($name)
                    if (-not $actions.State.Locked) { throw 'task neutralization occurred outside installer mutex' }
                    $actions.State.NeutralizeCalls++
                    $actions.State.Events += 'neutralize'
                    $actions.State.Tasks.Remove($name)
                }.GetNewClosure()
                $actions.ProtectStateTree = {
                    param($path)
                    throw 'simulated ACL repair failure'
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'simulated ACL repair failure' 'ACL failure propagates during preflight'
                Assert-Equal 0 $actions.State.NeutralizeCalls 'preflight ACL failure occurs before task neutralization'
                Assert-Equal 1 $actions.State.Tasks.Count 'preflight ACL failure preserves existing task posture'
                Assert-Equal 0 $actions.State.RegisterCalls 'failed security repair does not recreate task'
                Assert-Equal 0 $actions.State.StartCalls 'failed security repair does not start task'
                Assert-Equal 'mutex:Global\QuickBooksServiceHostAutoUpdate|release' ($actions.State.Events -join '|') 'preflight security failure releases mutex without task mutation'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.NeutralizeTask = { param($name) if (-not $actions.State.Locked) { throw 'neutralize outside lock' }; $actions.State.NeutralizeCalls++; $actions.State.Events += 'neutralize' }.GetNewClosure()
                $actions.ProtectStateTree = { param($path) $actions.State.SecurityPasses++; $actions.State.Events += 'secure' }.GetNewClosure()
                $actions.RegisterTask = { param($plan) $actions.State.Events += 'register'; $actions.State.RegisterCalls++; $actions.State.Tasks[$plan.TaskName] = $plan }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                $neutralizeIndex = [Array]::IndexOf($actions.State.Events, 'neutralize')
                $secureIndex = [Array]::IndexOf($actions.State.Events, 'secure')
                $registerIndex = [Array]::IndexOf($actions.State.Events, 'register')
                Assert-True ($secureIndex -gt 0 -and $secureIndex -lt $neutralizeIndex) 'verified staging security precedes task neutralization'
                Assert-True ($registerIndex -gt $neutralizeIndex) 'task recreated only after neutralization and promotion'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'late-failure-neutralization' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.NeutralizeTask = {
                    param($taskName)
                    $actions.State.NeutralizeCalls++
                    $actions.State.Events += "neutralize:$($actions.State.NeutralizeCalls)"
                    if (-not $actions.State.Locked) { throw 'late task neutralization occurred outside installer mutex' }
                    $actions.State.Tasks.Remove($taskName)
                }.GetNewClosure()
                $actions.StartTask = { param($taskName) $actions.State.StartCalls++; $actions.State.Events += 'start-failure'; throw 'late start failure' }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'late start failure' 'task-start failure remains primary'
                Assert-Equal 2 $actions.State.NeutralizeCalls 'task-start failure neutralizes the registered task'
                Assert-Equal 0 $actions.State.Tasks.Count 'task remains absent after start failure'
                $lockEvent = 'mutex:Global\QuickBooksServiceHostAutoUpdate'
                Assert-Equal "$lockEvent|neutralize:1|release|start-failure|$lockEvent|neutralize:2|release" `
                    ($actions.State.Events -join '|') 'late neutralization reacquires the shared mutex and releases it afterward'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.GetTask = { param($taskName) $null }
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } `
                    'Scheduled Task was not found after registration: QuickBooksServiceHost Auto Start and Update' `
                    'task verification failure remains primary'
                Assert-Equal 2 $actions.State.NeutralizeCalls 'task verification failure neutralizes the registered task'
                Assert-Equal 0 $actions.State.Tasks.Count 'task remains absent after task verification failure'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.StartTask = { param($taskName) $actions.State.StartCalls++ }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } `
                    "Installed host process was not found: $($fixture.HostPath)" 'host verification failure remains primary'
                Assert-Equal 2 $actions.State.NeutralizeCalls 'host verification failure neutralizes the registered task'
                Assert-Equal 0 $actions.State.Tasks.Count 'task remains absent after host verification failure'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.NeutralizeTask = {
                    param($taskName)
                    $actions.State.NeutralizeCalls++
                    if ($actions.State.NeutralizeCalls -gt 1) { throw 'late neutralization failure' }
                    $actions.State.Tasks.Remove($taskName)
                }.GetNewClosure()
                $actions.StartTask = { param($taskName) $actions.State.StartCalls++; throw 'primary late start failure' }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'failed late neutralization does not claim success'
                Assert-Equal 'primary late start failure' $caught.Exception.Message 'neutralization failure does not replace the primary failure'
                Assert-Equal 'late neutralization failure' $caught.Exception.Data['TaskNeutralizationFailure'] 'secondary neutralization failure is retained as diagnostic metadata'
                Assert-Equal 1 $actions.State.Tasks.Count 'failed neutralization leaves the registered task visible as an unsafe residual'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $originalMutex = $actions.Mutex
                $mutexAttempts = [pscustomobject]@{ Count = 0 }
                $actions.Mutex = {
                    param($name, $waitMilliseconds, $action)
                    $mutexAttempts.Count++
                    if ($mutexAttempts.Count -gt 1) { throw 'late mutex reacquisition failure' }
                    & $originalMutex $name $waitMilliseconds $action
                }.GetNewClosure()
                $actions.StartTask = { param($taskName) $actions.State.StartCalls++; throw 'primary start before mutex failure' }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'failed late mutex reacquisition does not claim success'
                Assert-Equal 'primary start before mutex failure' $caught.Exception.Message 'mutex reacquisition failure does not replace the primary failure'
                Assert-Equal 'late mutex reacquisition failure' $caught.Exception.Data['TaskNeutralizationFailure'] 'mutex reacquisition failure is retained as diagnostic metadata'
                Assert-Equal 1 $actions.State.NeutralizeCalls 'failed mutex reacquisition does not neutralize outside the lock'
                Assert-Equal 1 $actions.State.Tasks.Count 'failed mutex reacquisition leaves the registered task visible as an unsafe residual'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'critical-state-revalidation' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $race = [pscustomobject]@{ Substituted = $false; StateWrites = 0 }
                $actions.ProtectStateTree = {
                    param($path)
                    if ($race.Substituted) { throw 'critical state substitution detected' }
                    $actions.State.SecurityPasses++
                }.GetNewClosure()
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    if ($destination.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase)) {
                        $race.StateWrites++
                        & $originalCopy $source $destination
                        $race.Substituted = $true
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'critical state substitution detected' 'substitution after initial pass is rejected'
                Assert-Equal 1 $race.StateWrites 'substitution is detected before any later protected-state write'
                Assert-Equal 0 $actions.State.RegisterCalls 'substitution prevents task registration'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $race = [pscustomobject]@{ Substituted = $false; PostSubstitutionChecks = 0; StageRemoveAttempts = 0; ManagerStage = '' }
                $actions.ProtectStateTree = {
                    param($path)
                    if ($race.Substituted) {
                        $race.PostSubstitutionChecks++
                        if ($race.PostSubstitutionChecks -eq 1) { throw 'original manager security failure' }
                        throw 'cleanup manager security failure'
                    }
                    $actions.State.SecurityPasses++
                }.GetNewClosure()
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq (Join-Path $fixture.Source 'service_host_manager.ps1')) {
                        $race.ManagerStage = $destination
                        $race.Substituted = $true
                    }
                }.GetNewClosure()
                $originalRemove = $actions.RemovePath
                $actions.RemovePath = {
                    param($path, [bool]$recurse)
                    if (-not [string]::IsNullOrWhiteSpace($race.ManagerStage) -and
                        $race.ManagerStage.StartsWith($path, [StringComparison]::OrdinalIgnoreCase)) {
                        $race.StageRemoveAttempts++
                    }
                    & $originalRemove $path $recurse
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'original manager security failure' 'cleanup preserves the original manager-stage validation failure'
                Assert-Equal 2 $race.PostSubstitutionChecks 'manager-stage cleanup performs a fresh security validation'
                Assert-Equal 0 $race.StageRemoveAttempts 'hostile manager-stage target is never passed to removal'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.ProtectStateTree = { param($path) $actions.State.SecurityPasses++; $actions.State.Events += 'secure' }.GetNewClosure()
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    if ($destination.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase)) { $actions.State.Events += 'state-write' }
                    & $originalCopy $source $destination
                }.GetNewClosure()
                $actions.RegisterTask = { param($plan) $actions.State.Events += 'register'; $actions.State.RegisterCalls++; $actions.State.Tasks[$plan.TaskName] = $plan }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                foreach ($index in 0..($actions.State.Events.Count - 1)) {
                    if ($actions.State.Events[$index] -in @('state-write', 'register')) {
                        Assert-True ($index -gt 0 -and $actions.State.Events[$index - 1] -eq 'secure') "fresh security validation immediately precedes $($actions.State.Events[$index])"
                    }
                }
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'installer-mutex' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $assertLocked = { param($name) if (-not $actions.State.Locked) { throw "$name mutation occurred outside installer mutex" }; $actions.State.Events += $name }.GetNewClosure()
                $originalEnsure = $actions.EnsureDirectory
                $actions.EnsureDirectory = { param($path) & $assertLocked 'directory'; & $originalEnsure $path }.GetNewClosure()
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = { param($source, $destination) & $assertLocked 'copy'; & $originalCopy $source $destination }.GetNewClosure()
                $originalRemove = $actions.RemovePath
                $actions.RemovePath = { param($path, [bool]$recurse) & $assertLocked 'remove'; & $originalRemove $path $recurse }.GetNewClosure()
                $actions.ProtectStateTree = { param($path) & $assertLocked 'secure'; $actions.State.SecurityPasses++ }.GetNewClosure()
                $actions.SetEnvironmentVariable = { param($name, $value) & $assertLocked 'environment'; $actions.State.Environment[$name] = [string]$value }.GetNewClosure()
                $actions.CreateShortcut = { param($path, $target, $workingDirectory) & $assertLocked 'shortcut'; $actions.State.Shortcut = [pscustomobject]@{ Path = $path; TargetPath = $target; WorkingDirectory = $workingDirectory } }.GetNewClosure()
                $actions.RegisterTask = { param($plan) & $assertLocked 'register'; $actions.State.RegisterCalls++; $actions.State.Tasks[$plan.TaskName] = $plan }.GetNewClosure()
                $actions.StartTask = {
                    param($taskName)
                    if ($actions.State.Locked) { throw 'task start occurred while installer mutex was held' }
                    $actions.State.Events += 'start'
                    $actions.State.StartCalls++
                    $actions.State.NextId++
                    $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = $actions.State.NextId; Path = $fixture.HostPath })
                }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 'Global\QuickBooksServiceHostAutoUpdate' $actions.State.MutexName 'installer shares manager mutex name'
                Assert-Equal 30000 $actions.State.MutexWaitMilliseconds 'installer uses bounded mutex wait'
                $releaseIndex = [Array]::IndexOf($actions.State.Events, 'release')
                $startIndex = [Array]::IndexOf($actions.State.Events, 'start')
                $registerIndex = [Array]::IndexOf($actions.State.Events, 'register')
                Assert-True ($registerIndex -gt 0 -and $registerIndex -lt $releaseIndex) 'task registration is mutex protected'
                Assert-True ($releaseIndex -gt $registerIndex -and $releaseIndex -lt $startIndex) 'mutex releases before task start'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.Mutex = { param($name, $waitMilliseconds, $action) throw "Timed out waiting for service host update lock: $name" }
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } `
                    'Timed out waiting for service host update lock: Global\QuickBooksServiceHostAutoUpdate' 'concurrent manager ownership fails clearly'
                Assert-Equal 0 $actions.State.CopyCalls 'concurrent ownership fails before copies'
                Assert-Equal 0 $actions.State.RemoveCalls 'concurrent ownership fails before filesystem removal'
                Assert-Equal 0 $actions.State.SecurityPasses 'concurrent ownership fails before state mutation'
                Assert-Equal 0 $actions.State.RegisterCalls 'concurrent ownership fails before task mutation'
                Assert-Equal 0 $actions.State.StartCalls 'concurrent ownership fails before task start'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'manifest-install' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $settingsBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $fixture.Source 'bridge.settings.psd1')))
                Invoke-FixtureInstall $fixture $actions
                Assert-True (Test-Path -LiteralPath $fixture.ManagerPath -PathType Leaf) 'manager installed at canonical path'
                Assert-True (Test-Path -LiteralPath $fixture.InstalledManifestPath -PathType Leaf) 'root manifest seeded'
                Assert-Equal 'release-install' ((Get-Content -Raw -LiteralPath $fixture.InstalledManifestPath | ConvertFrom-Json).release_id) 'installed release id'
                Assert-Equal $settingsBefore ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $fixture.Source 'bridge.settings.psd1')))) 'bridge settings preserved byte for byte'
                Assert-Equal $fixture.HostPath $actions.State.Shortcut.TargetPath 'shortcut target'
                Assert-Equal $fixture.Install $actions.State.Shortcut.WorkingDirectory 'shortcut working directory'
                Assert-Equal (Join-Path $fixture.Install 'QuickBooksConnectorCli.exe') $actions.State.Environment['QUOTE_MODULEV2_QB_CONNECTOR_CLI'] 'connector environment'
                Assert-Equal 'fixture-token' $actions.State.Environment['QB_BRIDGE_TOKEN'] 'bridge token'
                Assert-Equal 'http://fixture:8742' $actions.State.Environment['QB_BRIDGE_ORIGIN'] 'bridge origin'
                Assert-Equal '8788' $actions.State.Environment['QB_BRIDGE_PORT'] 'bridge port'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'corrupt-manager' {
            $fixture = New-InstallerFixture
            try {
                [IO.File]::WriteAllText((Join-Path $fixture.Source 'service_host_manager.ps1'), '# mangler')
                $actions = New-InjectedActions $fixture
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Staged manager verification failed: service_host_manager.ps1' 'corrupt manager rejected'
                Assert-True ($actions.State.CopyCalls -gt 0) 'manager validation occurs against the isolated staged copy'
                Assert-Equal 0 $actions.State.StopCalls 'manager validation precedes host stop'
                Assert-Equal 0 $actions.State.RegisterCalls 'manager validation precedes registration'
                Assert-Equal 0 $actions.State.StartCalls 'manager validation precedes startup'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'corrupt-installed-manager' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $originalMove = $actions.MovePath
                $actions.MovePath = {
                    param($source, $destination)
                    & $originalMove $source $destination
                    if ($destination -ieq $fixture.ManagerPath) {
                        [IO.File]::WriteAllText($destination, '# mangler')
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-FixtureInstall $fixture $actions
                } 'Promoted manager verification failed: service_host_manager.ps1' 'promoted manager corruption rejected'
                Assert-Equal 0 $actions.State.RegisterCalls 'installed manager verification precedes registration'
                Assert-Equal 0 $actions.State.StartCalls 'installed manager verification precedes task startup'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'stale-cleanup' {
            $fixture = New-InstallerFixture
            try {
                New-Item -ItemType Directory -Force -Path (Join-Path $fixture.Install 'old') | Out-Null
                [IO.File]::WriteAllText((Join-Path $fixture.Install 'stale.dll'), 'stale')
                [IO.File]::WriteAllText((Join-Path $fixture.Install 'old\stale.config'), 'stale')
                $actions = New-InjectedActions $fixture
                Invoke-FixtureInstall $fixture $actions
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Install 'stale.dll'))) 'root stale file removed'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Install 'old'))) 'stale directory removed'
                $relative = @(Get-ChildItem -LiteralPath $fixture.Install -Recurse -File | ForEach-Object { $_.FullName.Substring($fixture.Install.Length).TrimStart('\').Replace('\', '/') } | Sort-Object)
                Assert-Equal 'nested/runtime.dll|QuickBooksConnectorCli.exe|QuickBooksServiceHost.exe' ($relative -join '|') 'runtime tree is manifest-exclusive'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'stop-race' {
            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                Set-RetainedPreviousFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 7; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $actions.State.ExitPollsRemaining = 2
                $actions.State.RequireExitBeforeCopy = $true
                Invoke-FixtureInstall $fixture $actions -StopWaitAttempts 4
                Assert-Equal 1 $actions.State.StopCalls 'host stopped once'
                Assert-Equal 2 $actions.State.WaitCalls 'installer polls delayed process exit'
                Assert-True $actions.State.HostExited 'copy begins only after expected-path host exits'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'stop-timeout' {
            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $taskName = 'QuickBooksServiceHost Auto Start and Update'
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 8; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $actions.State.NeverExit = $true
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions -StopWaitAttempts 3 } "Installed host process did not exit: $($fixture.HostPath)" 'bounded stop timeout'
                Assert-Equal 1 $actions.State.StopCalls 'timeout stops once'
                Assert-Equal 3 $actions.State.WaitCalls 'timeout uses bounded polling'
                Assert-True ($actions.State.CopyCalls -ge 5) 'timeout occurs only after complete candidate staging'
                Assert-OldReleaseRestored $fixture 'timeout preserves installed release'
                Assert-Equal 1 $actions.State.RegisterCalls 'timeout re-registers old task'
                Assert-Equal 0 $actions.State.StartCalls 'timeout recovery never invokes the manager task'
                Assert-Equal 0 $actions.State.DirectStartCalls 'timeout verifies the still-running installed host without duplicate launch'
                Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count 'timeout leaves exactly one old host'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'transactional-install' {
            $taskName = 'QuickBooksServiceHost Auto Start and Update'

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq (Join-Path $fixture.Source 'nested\runtime.dll') -and
                        $destination.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase)) {
                        [IO.File]::WriteAllText($destination, 'corrupt-stage')
                    }
                }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'corrupt staged runtime fails installation'
                Assert-Equal 'Staged file verification failed: nested/runtime.dll' $caught.Exception.Message 'corrupt stage failure is explicit'
                Assert-Equal 0 $actions.State.StopCalls 'corrupt staged runtime never stops the old host'
                Assert-OldReleaseRestored $fixture 'corrupt staged runtime preserves'
                Assert-Equal 1 $actions.State.Tasks.Count 'corrupt staged runtime preserves existing task posture'
                Assert-Equal 0 $actions.State.RegisterCalls 'corrupt staged runtime needs no task recreation'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 28; Path = $fixture.HostPath })
                $originalNeutralize = $actions.NeutralizeTask
                $neutralizeAttempt = [pscustomobject]@{ Count = 0 }
                $actions.NeutralizeTask = {
                    param($name)
                    $neutralizeAttempt.Count++
                    if ($neutralizeAttempt.Count -eq 2) { throw 'simulated rollback neutralize before unregister' }
                    & $originalNeutralize $name
                }.GetNewClosure()
                $actions.StartTask = { param($name) $actions.State.StartCalls++; throw 'simulated candidate start failure' }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions } catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'rollback neutralize fail-closed case throws'
                Assert-Equal 'simulated candidate start failure' $caught.Exception.Message 'candidate start failure remains primary'
                Assert-Equal 'simulated rollback neutralize before unregister' $caught.Exception.Data['TaskNeutralizationFailure'] 'armed-task neutralization error is separate diagnostic'
                Assert-True ([string]$caught.Exception.Data['RollbackFailure'] -match 'could not be confirmed absent') 'rollback reports armed task fail-closed state'
                Assert-Equal 'host' ([IO.File]::ReadAllText($fixture.HostPath)) 'armed-task rollback does not mutate promoted runtime'
                Assert-Equal 'old-host' ([IO.File]::ReadAllText((Join-Path $fixture.PreviousPath 'Payload\QuickBooksServiceHost.exe'))) 'armed-task rollback leaves retained old runtime available'
                Assert-Equal 1 $actions.State.Tasks.Count 'armed candidate task remains visible for operator recovery'
                Assert-Equal 1 $actions.State.StopCalls 'rollback does not stop processes after neutralization remains armed'
                Assert-Equal 0 $actions.State.DirectStartCalls 'fail-closed rollback does not launch restored host'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq $fixture.InstalledManifestPath -and
                        $destination -match '[\\/]PreparedPrevious[\\/]') {
                        [IO.File]::WriteAllText($destination, '{"release_id":"corrupted-previous"}')
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Previous release manifest snapshot verification failed: release.manifest.json' 'corrupt retained manifest snapshot fails closed'
                Assert-Equal 0 $actions.State.StopCalls 'corrupt retained manifest snapshot precedes host stop'
                Assert-OldReleaseRestored $fixture 'corrupt retained manifest snapshot preserves'
                Assert-Equal 0 $actions.State.RegisterCalls 'corrupt retained manifest snapshot never registers a task'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $originalReadManifest = $actions.ReadManifest
                $actions.ReadManifest = {
                    param($path)
                    $parsed = & $originalReadManifest $path
                    if ($path -ieq (Join-Path $fixture.Source 'release.manifest.json')) {
                        $replacement = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
                        $replacement.release_id = 'publication-replaced-mid-read'
                        $replacement | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
                    }
                    $parsed
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Release manifest changed during validation: release.manifest.json' 'manifest replacement during validation fails closed'
                Assert-Equal 0 $actions.State.StopCalls 'manifest replacement during validation precedes host stop'
                Assert-OldReleaseRestored $fixture 'manifest replacement preserves'
                Assert-Equal 0 $actions.State.RegisterCalls 'manifest replacement never registers a task'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq $fixture.ManagerPath -and
                        $destination -match '[\\/]PreparedPrevious[\\/]') {
                        [IO.File]::WriteAllText($destination, '# corrupted previous manager')
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Previous manager snapshot verification failed: service_host_manager.ps1' 'corrupt retained manager snapshot fails closed'
                Assert-Equal 0 $actions.State.StopCalls 'corrupt retained manager snapshot precedes host stop'
                Assert-OldReleaseRestored $fixture 'corrupt retained manager snapshot preserves'
                Assert-Equal 0 $actions.State.RegisterCalls 'corrupt retained manager snapshot never registers a task'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    if ($source -ieq (Join-Path $fixture.Source 'nested\runtime.dll')) {
                        Remove-Item -LiteralPath $source -Force
                    }
                    & $originalCopy $source $destination
                }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'mid-stage publication disappearance fails installation'
                Assert-Equal 0 $actions.State.StopCalls 'mid-stage publication disappearance never stops the old host'
                Assert-OldReleaseRestored $fixture 'mid-stage disappearance preserves'
                Assert-Equal 0 $actions.State.RegisterCalls 'mid-stage disappearance never registers a task'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                New-Item -ItemType Directory -Force -Path $fixture.PreviousPath | Out-Null
                [IO.File]::WriteAllText((Join-Path $fixture.PreviousPath 'stale.txt'), 'stale')
                $actions = New-InjectedActions $fixture
                $originalMove = $actions.MovePath
                $actions.MovePath = {
                    param($source, $destination)
                    if ($source -ieq $fixture.PreviousPath) { throw 'stale Previous cleanup failure' }
                    & $originalMove $source $destination
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'stale Previous cleanup failure' 'stale Previous cleanup fails fast'
                Assert-Equal 0 $actions.State.StopCalls 'stale Previous cleanup failure precedes host stop'
                Assert-OldReleaseRestored $fixture 'stale Previous cleanup preserves'
                Assert-Equal 0 $actions.State.RegisterCalls 'stale Previous cleanup failure never registers a task'
            }
            finally { Remove-InstallerFixture $fixture }

            foreach ($failure in @('move-current', 'promote-payload', 'promote-manager', 'promote-manifest')) {
                $fixture = New-InstallerFixture
                try {
                    Set-InstalledReleaseFixture $fixture
                    $actions = New-InjectedActions $fixture
                    $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 7; Path = $fixture.HostPath })
                    $actions.State.HostExited = $false
                    $originalMove = $actions.MovePath
                    $failed = [pscustomobject]@{ Value = $false }
                    $actions.MovePath = {
                        param($source, $destination)
                        $isCandidateStage = $source.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase) -and
                            -not $source.StartsWith($fixture.PreviousPath, [StringComparison]::OrdinalIgnoreCase)
                        $matches = switch ($failure) {
                            'move-current' { $source -ieq $fixture.Install }
                            'promote-payload' { $isCandidateStage -and (Split-Path -Leaf $source) -eq 'Payload' }
                            'promote-manager' { $isCandidateStage -and $destination -ieq $fixture.ManagerPath }
                            'promote-manifest' { $isCandidateStage -and $destination -ieq $fixture.InstalledManifestPath }
                        }
                        if ($matches -and -not $failed.Value) {
                            $failed.Value = $true
                            throw "simulated $failure failure"
                        }
                        & $originalMove $source $destination
                    }.GetNewClosure()
                    Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } "simulated $failure failure" "$failure remains primary"
                    Assert-Equal 1 $actions.State.StopCalls "$failure occurs after exactly one old-host stop"
                    Assert-OldReleaseRestored $fixture "$failure rollback restores"
                    Assert-Equal 1 $actions.State.Tasks.Count "$failure rollback registers the old task"
                    Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count "$failure rollback restarts exactly the old expected path"
                    Assert-True (-not (Test-Path -LiteralPath $fixture.PreviousPath)) "$failure rollback consumes Previous"
                }
                finally { Remove-InstallerFixture $fixture }
            }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 8; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $registerAttempt = [pscustomobject]@{ Count = 0 }
                $actions.RegisterTask = {
                    param($plan)
                    $registerAttempt.Count++
                    $actions.State.RegisterCalls++
                    if ($registerAttempt.Count -eq 1) { throw 'simulated register failure' }
                    $actions.State.Tasks[$plan.TaskName] = $plan
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'simulated register failure' 'register failure remains primary'
                Assert-OldReleaseRestored $fixture 'register failure rollback restores'
                Assert-Equal 2 $actions.State.RegisterCalls 'register failure rolls back with the old task definition'
                Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count 'register failure restarts old host'
            }
            finally { Remove-InstallerFixture $fixture }

            foreach ($failure in @('start', 'health')) {
                $fixture = New-InstallerFixture
                try {
                    Set-InstalledReleaseFixture $fixture
                    $actions = New-InjectedActions $fixture
                    $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 9; Path = $fixture.HostPath })
                    $actions.State.HostExited = $false
                    $originalStart = $actions.StartTask
                    $startAttempt = [pscustomobject]@{ Count = 0 }
                    $actions.StartTask = {
                        param($name)
                        $startAttempt.Count++
                        if ($startAttempt.Count -eq 1) {
                            $actions.State.StartCalls++
                            if ($failure -eq 'start') { throw 'simulated new start failure' }
                            return
                        }
                        & $originalStart $name
                    }.GetNewClosure()
                    $expected = if ($failure -eq 'start') { 'simulated new start failure' } else { "Installed host process was not found: $($fixture.HostPath)" }
                    Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } $expected "$failure failure remains primary"
                    Assert-OldReleaseRestored $fixture "$failure failure rollback restores"
                    Assert-Equal 1 $actions.State.StartCalls "$failure failure starts candidate task only"
                    Assert-Equal 1 $actions.State.DirectStartCalls "$failure failure directly starts restored host"
                    Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count "$failure failure restarts exact old path"
                }
                finally { Remove-InstallerFixture $fixture }
            }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 10; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $actions.StartTask = {
                    param($name)
                    $actions.State.StartCalls++
                    throw 'primary new start failure'
                }.GetNewClosure()
                $actions.StartInstalledHost = { param($hostPath, $workingDirectory) $actions.State.DirectStartCalls++; throw 'rollback old start failure' }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'rollback-start dual failure is reported'
                Assert-Equal 'primary new start failure' $caught.Exception.Message 'rollback-start failure does not replace primary'
                Assert-Equal 'rollback old start failure' $caught.Exception.Data['RollbackFailure'] 'rollback-start failure is separate diagnostic metadata'
                Assert-OldReleaseRestored $fixture 'rollback-start failure still restores bytes'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 11; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $stageRoots = New-Object Collections.Generic.List[string]
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source.StartsWith($fixture.Source, [StringComparison]::OrdinalIgnoreCase) -and
                        $destination.StartsWith($fixture.StatePath, [StringComparison]::OrdinalIgnoreCase)) {
                        $relative = $destination.Substring($fixture.StatePath.Length).TrimStart('\')
                        $root = ($relative -split '\\')[0]
                        if (-not $stageRoots.Contains($root)) { $stageRoots.Add($root) }
                        $actions.State.Events += "stage:$([IO.Path]::GetFileName($destination))"
                    }
                }.GetNewClosure()
                $originalStop = $actions.StopProcess
                $actions.StopProcess = { param($process) $actions.State.Events += 'stop'; & $originalStop $process }.GetNewClosure()
                $originalMove = $actions.MovePath
                $actions.MovePath = {
                    param($source, $destination)
                    if ($source -ieq $fixture.Install) { $actions.State.Events += 'move-current' }
                    elseif ((Split-Path -Leaf $source) -eq 'Payload') { $actions.State.Events += 'promote-payload' }
                    elseif ($destination -ieq $fixture.ManagerPath) { $actions.State.Events += 'promote-manager' }
                    elseif ($destination -ieq $fixture.InstalledManifestPath) { $actions.State.Events += 'promote-manifest' }
                    & $originalMove $source $destination
                }.GetNewClosure()
                $actions.RegisterTask = { param($plan) $actions.State.Events += 'register'; $actions.State.RegisterCalls++; $actions.State.Tasks[$plan.TaskName] = $plan }.GetNewClosure()
                $originalStart = $actions.StartTask
                $actions.StartTask = { param($name) $actions.State.Events += 'start'; & $originalStart $name }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 1 $stageRoots.Count 'one unique stage root receives all candidate files'
                Assert-True ($stageRoots[0] -match '^[0-9a-f]{32}$') 'stage root is GUID named and unpredictable'
                Assert-Equal 'host' ([IO.File]::ReadAllText($fixture.HostPath)) 'success promotes candidate runtime'
                Assert-Equal 'release-install' ((Get-Content -Raw -LiteralPath $fixture.InstalledManifestPath | ConvertFrom-Json).release_id) 'success promotes candidate manifest'
                Assert-Equal 'old-host' ([IO.File]::ReadAllText((Join-Path $fixture.PreviousPath 'Payload\QuickBooksServiceHost.exe'))) 'success retains previous runtime'
                Assert-Equal '# old manager' ([IO.File]::ReadAllText((Join-Path $fixture.PreviousPath 'Manager\service_host_manager.ps1'))) 'success retains previous manager'
                Assert-Equal 'release-old' ((Get-Content -Raw -LiteralPath (Join-Path $fixture.PreviousPath 'release.manifest.json') | ConvertFrom-Json).release_id) 'success retains previous manifest'
                $lastStage = [Array]::LastIndexOf($actions.State.Events, @($actions.State.Events | Where-Object { $_ -like 'stage:*' } | Select-Object -Last 1)[0])
                $stop = [Array]::IndexOf($actions.State.Events, 'stop')
                $move = [Array]::IndexOf($actions.State.Events, 'move-current')
                $promote = [Array]::IndexOf($actions.State.Events, 'promote-payload')
                $register = [Array]::IndexOf($actions.State.Events, 'register')
                $release = [Array]::LastIndexOf($actions.State.Events, 'release')
                $start = [Array]::IndexOf($actions.State.Events, 'start')
                Assert-True ($lastStage -ge 0 -and $lastStage -lt $stop -and $stop -lt $move -and $move -lt $promote -and $promote -lt $register -and $register -lt $release -and $release -lt $start) 'verified stage precedes stop, move, promote, register, release, and start'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'transaction-hardening' {
            $taskName = 'QuickBooksServiceHost Auto Start and Update'

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                Set-RetainedPreviousFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 20; Path = $fixture.HostPath })
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq (Join-Path $fixture.Source 'nested\runtime.dll')) { [IO.File]::WriteAllText($destination, 'corrupt-stage') }
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Staged file verification failed: nested/runtime.dll' 'preflight stage corruption remains primary'
                Assert-Equal 0 $actions.State.StopCalls 'preflight corruption never attempts stop'
                Assert-Equal 1 $actions.State.Tasks.Count 'preflight corruption restores the task posture'
                Assert-Equal 1 @($actions.State.Processes).Count 'preflight corruption leaves the running host alone'
                Assert-RetainedPreviousFixture $fixture 'preflight corruption preserves existing Previous'
                Assert-OldReleaseRestored $fixture 'preflight corruption preserves current release'
            }
            finally { Remove-InstallerFixture $fixture }

            foreach ($missing in @('manager', 'manifest')) {
                $fixture = New-InstallerFixture
                try {
                    Set-InstalledReleaseFixture $fixture
                    if ($missing -eq 'manager') { Remove-Item -LiteralPath $fixture.ManagerPath -Force }
                    else { Remove-Item -LiteralPath $fixture.InstalledManifestPath -Force }
                    $actions = New-InjectedActions $fixture
                    $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                    $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 21; Path = $fixture.HostPath })
                    $expected = if ($missing -eq 'manager') { 'Existing runtime requires a verified manager snapshot: service_host_manager.ps1' } else { 'Existing runtime requires a verified release manifest snapshot: release.manifest.json' }
                    Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } $expected "missing old $missing fails pre-stop"
                    Assert-Equal 0 $actions.State.StopCalls "missing old $missing does not stop host"
                    Assert-Equal 1 $actions.State.Tasks.Count "missing old $missing restores task posture"
                    Assert-Equal 1 @($actions.State.Processes).Count "missing old $missing leaves host running"
                }
                finally { Remove-InstallerFixture $fixture }
            }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                Set-RetainedPreviousFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $actions.State.Processes = @(
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 22; Path = $fixture.HostPath },
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 23; Path = $fixture.HostPath }
                )
                $stopIndex = [pscustomobject]@{ Value = 0 }
                $actions.StopProcess = {
                    param($process)
                    $actions.State.StopCalls++
                    $stopIndex.Value++
                    if ($stopIndex.Value -eq 1) {
                        $actions.State.Processes = @($actions.State.Processes | Where-Object { $_.Id -ne $process.Id })
                        return
                    }
                    throw 'simulated partial stop failure'
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'simulated partial stop failure' 'partial stop failure remains primary'
                Assert-Equal 2 $actions.State.StopCalls 'partial stop records both stop attempts'
                Assert-Equal 1 $actions.State.RegisterCalls 'partial stop re-registers old task'
                Assert-Equal 0 $actions.State.StartCalls 'partial stop recovery never invokes manager task'
                Assert-Equal 0 $actions.State.DirectStartCalls 'partial stop verifies the surviving installed host without duplicate launch'
                Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count 'partial stop recovers exactly one old host'
                Assert-OldReleaseRestored $fixture 'partial stop keeps current tree untouched'
                Assert-RetainedPreviousFixture $fixture 'partial stop restores pre-existing Previous'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                Set-RetainedPreviousFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 26; Path = $fixture.HostPath })
                $actions.StopProcess = { param($process) $actions.State.StopCalls++; throw 'simulated stop recovery failure' }.GetNewClosure()
                $originalMove = $actions.MovePath
                $actions.MovePath = {
                    param($source, $destination)
                    if ((Split-Path -Leaf $source) -eq 'DisplacedPrevious' -and $destination -ieq $fixture.PreviousPath) {
                        throw 'simulated displaced Previous restore failure'
                    }
                    & $originalMove $source $destination
                }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions } catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'failed displaced Previous recovery throws'
                Assert-Equal 'simulated stop recovery failure' $caught.Exception.Message 'stop failure remains primary when Previous restore fails'
                Assert-Equal 'simulated displaced Previous restore failure' $caught.Exception.Data['RollbackFailure'] 'Previous restore failure is separate recovery diagnostic'
                Assert-True (-not [string]::IsNullOrWhiteSpace([string]$caught.Exception.Data['RecoveryStatePath'])) 'recoverable GUID state path is reported'
                Assert-True ([string]$caught.Exception.Data['StageCleanupFailure'] -match 'preserved') 'stage cleanup diagnostic reports intentional preservation'
                $retained = Join-Path ([string]$caught.Exception.Data['RecoveryStatePath']) 'DisplacedPrevious\Payload\retained.txt'
                Assert-Equal 'retained-payload' ([IO.File]::ReadAllText($retained)) 'failed Previous restore preserves recoverable retained payload'
                Assert-Equal 0 $actions.State.StartCalls 'failed Previous recovery never invokes manager task'
                Assert-Equal 0 $actions.State.DirectStartCalls 'failed Previous recovery verifies the still-running old installed host'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 27; Path = $fixture.HostPath })
                $originalNeutralize = $actions.NeutralizeTask
                $neutralizeAttempt = [pscustomobject]@{ Count = 0 }
                $actions.NeutralizeTask = {
                    param($name)
                    $neutralizeAttempt.Count++
                    & $originalNeutralize $name
                    if ($neutralizeAttempt.Count -eq 2) { throw 'simulated rollback neutralize-after-unregister failure' }
                }.GetNewClosure()
                $originalMove = $actions.MovePath
                $managerPromotionFailed = [pscustomobject]@{ Value = $false }
                $actions.MovePath = {
                    param($source, $destination)
                    if ($destination -ieq $fixture.ManagerPath -and -not $managerPromotionFailed.Value) {
                        $managerPromotionFailed.Value = $true
                        throw 'simulated manager promotion failure'
                    }
                    & $originalMove $source $destination
                }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions } catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'rollback neutralize mutate-then-throw case fails forward install'
                Assert-Equal 'simulated manager promotion failure' $caught.Exception.Message 'promotion failure remains primary through rollback neutralize error'
                Assert-Equal 'simulated rollback neutralize-after-unregister failure' $caught.Exception.Data['TaskNeutralizationFailure'] 'rollback neutralize error is separate diagnostic'
                Assert-OldReleaseRestored $fixture 'rollback continues restoring state after neutralize error'
                Assert-Equal 1 $actions.State.Tasks.Count 'rollback recreates task after neutralize error'
                Assert-Equal 0 $actions.State.StartCalls 'rollback recovery never invokes manager task'
                Assert-Equal 1 $actions.State.DirectStartCalls 'rollback directly restarts restored host after neutralize error'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                Set-RetainedPreviousFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.State.Tasks[$taskName] = [pscustomobject]@{ TaskName = $taskName }
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 25; Path = $fixture.HostPath })
                $originalNeutralize = $actions.NeutralizeTask
                $actions.NeutralizeTask = {
                    param($name)
                    & $originalNeutralize $name
                    throw 'simulated neutralize-after-unregister failure'
                }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'simulated neutralize-after-unregister failure' 'neutralize mutate-then-throw remains primary'
                Assert-Equal 0 $actions.State.StopCalls 'neutralize failure occurs before stop attempt'
                Assert-Equal 1 $actions.State.RegisterCalls 'neutralize failure reconciles and recreates old task'
                Assert-Equal 0 $actions.State.StartCalls 'neutralize failure does not start an already-running old host'
                Assert-Equal 1 $actions.State.Tasks.Count 'neutralize failure restores exact task posture'
                Assert-Equal 1 @($actions.State.Processes).Count 'neutralize failure leaves running host untouched'
                Assert-RetainedPreviousFixture $fixture 'neutralize failure restores pre-existing Previous'
            }
            finally { Remove-InstallerFixture $fixture }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $actions.GetVolumeIdentity = { param($path) if ($path -ieq $fixture.Install) { '\\?\Volume{install-mounted-under-c}' } else { '\\?\Volume{state-on-c}' } }.GetNewClosure()
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'InstallPath and StatePath must be on the same volume for transactional directory moves.' 'cross-volume install rejected'
                Assert-Equal 0 $actions.State.NeutralizeCalls 'cross-volume rejection precedes task mutation'
                Assert-Equal 0 $actions.State.StopCalls 'cross-volume rejection precedes stop'
                Assert-Equal 0 $actions.State.MoveCalls 'cross-volume rejection precedes moves'
                Assert-Equal 0 $actions.State.CopyCalls 'cross-volume rejection precedes copies'
                Assert-Equal 0 $actions.State.SecurityPasses 'cross-volume rejection precedes state mutation'
            }
            finally { Remove-InstallerFixture $fixture }

            foreach ($point in @('current', 'candidate')) {
                $fixture = New-InstallerFixture
                try {
                    Set-InstalledReleaseFixture $fixture
                    $actions = New-InjectedActions $fixture
                    $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 24; Path = $fixture.HostPath })
                    $originalMove = $actions.MovePath
                    $thrown = [pscustomobject]@{ Value = $false }
                    $actions.MovePath = {
                        param($source, $destination)
                        $isCandidate = (Split-Path -Leaf $source) -eq 'Payload' -and $source -ne $fixture.Install
                        $matches = ($point -eq 'current' -and $source -ieq $fixture.Install) -or ($point -eq 'candidate' -and $isCandidate -and $destination -ieq $fixture.Install)
                        & $originalMove $source $destination
                        if ($matches -and -not $thrown.Value) { $thrown.Value = $true; throw "simulated $point move-then-throw" }
                    }.GetNewClosure()
                    Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } "simulated $point move-then-throw" "$point move-then-throw remains primary"
                    Assert-OldReleaseRestored $fixture "$point move-then-throw rollback restores"
                    Assert-Equal 1 $actions.State.Tasks.Count "$point move-then-throw restores task"
                    Assert-Equal 1 @($actions.State.Processes | Where-Object { $_.Path -ieq $fixture.HostPath }).Count "$point move-then-throw restores host"
                    Assert-True (-not (Test-Path -LiteralPath $fixture.PreviousPath)) "$point move-then-throw consumes Previous"
                    Assert-True ($actions.State.RuntimeSecurityPasses -ge 1) "$point move-then-throw reapplies runtime ACL after restore"
                }
                finally { Remove-InstallerFixture $fixture }
            }

            $fixture = New-InstallerFixture
            try {
                Set-InstalledReleaseFixture $fixture
                $actions = New-InjectedActions $fixture
                $originalCopy = $actions.CopyFile
                $actions.CopyFile = {
                    param($source, $destination)
                    & $originalCopy $source $destination
                    if ($source -ieq (Join-Path $fixture.Source 'nested\runtime.dll')) { [IO.File]::WriteAllText($destination, 'corrupt-stage') }
                }.GetNewClosure()
                $originalRemove = $actions.RemovePath
                $actions.RemovePath = {
                    param($path, [bool]$recurse)
                    if ((Split-Path -Leaf $path) -match '^[0-9a-f]{32}$') { throw 'simulated GUID cleanup failure' }
                    & $originalRemove $path $recurse
                }.GetNewClosure()
                $caught = $null
                try { Invoke-FixtureInstall $fixture $actions } catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'cleanup failure case throws'
                Assert-Equal 'Staged file verification failed: nested/runtime.dll' $caught.Exception.Message 'cleanup failure does not mask primary'
                Assert-Equal 'simulated GUID cleanup failure' $caught.Exception.Data['StageCleanupFailure'] 'cleanup failure is separate diagnostic metadata'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'reinstall-idempotent' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                Invoke-FixtureInstall $fixture $actions
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 2 $actions.State.RegisterCalls 'two force registrations'
                Assert-Equal 1 $actions.State.Tasks.Count 'one logical task'
                Assert-Equal 2 $actions.State.StartCalls 'two starts'
                Assert-Equal 1 $actions.State.StopCalls 'reinstall stops old host'
                Assert-Equal 1 @($actions.State.Processes).Count 'one process remains'
                Assert-Equal $fixture.HostPath $actions.State.Processes[0].Path 'remaining host uses installed path'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        default { throw "Unknown scenario: $Name" }
    }
}

$allScenarios = @('static', 'task-plan', 'state-security', 'runtime-acl', 'native-takeown', 'identity-mismatch', 'identity-binding', 'task-neutralization', 'late-failure-neutralization', 'critical-state-revalidation', 'installer-mutex', 'manifest-install', 'corrupt-manager', 'corrupt-installed-manager', 'stale-cleanup', 'stop-race', 'stop-timeout', 'transactional-install', 'transaction-hardening', 'reinstall-idempotent')
if ($Scenario -eq 'all') { foreach ($name in $allScenarios) { Run-Scenario $name } }
else { Run-Scenario $Scenario }
Write-Host 'Install service host script checks passed.'
