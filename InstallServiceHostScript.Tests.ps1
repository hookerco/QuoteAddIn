[CmdletBinding()]
param(
    [ValidateSet('static', 'task-plan', 'state-security', 'identity-mismatch', 'identity-binding', 'installer-mutex', 'manifest-install', 'corrupt-manager', 'corrupt-installed-manager', 'stale-cleanup', 'stop-race', 'stop-timeout', 'reinstall-idempotent', 'all')]
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
    }
}

function Remove-InstallerFixture($Fixture) {
    Remove-Item -LiteralPath $Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
}

function New-InjectedActions {
    param($Fixture)
    $state = [pscustomobject]@{
        CopyCalls = 0
        RemoveCalls = 0
        RegisterCalls = 0
        StartCalls = 0
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
        Locked = $false
        MutexName = ''
        MutexWaitMilliseconds = 0
        Events = @()
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
            if ($state.RequireExitBeforeCopy -and -not $state.HostExited) { throw 'runtime copy happened before host exit' }
            $state.CopyCalls++
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }.GetNewClosure()
        RemovePath = {
            param($path, [bool]$recurse)
            if ($state.RequireExitBeforeCopy -and -not $state.HostExited) { throw 'runtime cleanup happened before host exit' }
            $state.RemoveCalls++
            Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction SilentlyContinue
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
        Mutex = {
            param($name, $waitMilliseconds, $action)
            $state.MutexName = $name
            $state.MutexWaitMilliseconds = $waitMilliseconds
            $state.Events += "mutex:$name"
            $state.Locked = $true
            try { & $action }
            finally { $state.Locked = $false; $state.Events += 'release' }
        }.GetNewClosure()
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
    if ($parameters.ContainsKey('StopWaitAttempts')) { $arguments.StopWaitAttempts = $StopWaitAttempts }
    if ($parameters.ContainsKey('ProtectStateTreeAction')) { $arguments.ProtectStateTreeAction = $Actions.ProtectStateTree }
    if ($parameters.ContainsKey('MutexAction')) { $arguments.MutexAction = $Actions.Mutex }
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
            $getAcl = { param($path) $acls[$path] }.GetNewClosure()
            $ownersTaken = @{}
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
                    Assert-Equal 2 $actions.State.SecurityPasses 'manager and state files re-verified before task registration'
                    $events.Add('register')
                    $actions.State.RegisterCalls++
                    $actions.State.Tasks[$plan.TaskName] = $plan
                }.GetNewClosure()
                Invoke-FixtureInstall $fixture $actions
                Assert-Equal 'secure:1|secure:2|register' ($events -join '|') 'security passes bracket ProgramData copies before task registration'
            }
            finally { Remove-InstallerFixture $fixture }
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
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions } 'Manager source verification failed: service_host_manager.ps1' 'corrupt manager rejected'
                Assert-Equal 0 $actions.State.CopyCalls 'manager validation precedes all copies'
                Assert-Equal 0 $actions.State.RemoveCalls 'manager validation precedes install cleanup'
                Assert-Equal 0 $actions.State.RegisterCalls 'manager validation precedes registration'
                Assert-Equal 0 $actions.State.StartCalls 'manager validation precedes startup'
            }
            finally { Remove-InstallerFixture $fixture }
        }
        'corrupt-installed-manager' {
            $fixture = New-InstallerFixture
            try {
                $actions = New-InjectedActions $fixture
                $actions.CopyFile = {
                    param($source, $destination)
                    $actions.State.CopyCalls++
                    $parent = Split-Path -Parent $destination
                    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                        New-Item -ItemType Directory -Force -Path $parent | Out-Null
                    }
                    Copy-Item -LiteralPath $source -Destination $destination -Force
                    if ($destination -ieq $fixture.ManagerPath) {
                        [IO.File]::WriteAllText($destination, '# mangler')
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-FixtureInstall $fixture $actions
                } 'Installed manager verification failed: service_host_manager.ps1' 'installed manager corruption rejected'
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
                [IO.File]::WriteAllText($fixture.HostPath, 'old-host')
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
                [IO.File]::WriteAllText($fixture.HostPath, 'old-host')
                $actions = New-InjectedActions $fixture
                $actions.State.Processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 8; Path = $fixture.HostPath })
                $actions.State.HostExited = $false
                $actions.State.NeverExit = $true
                Assert-ThrowsMessage { Invoke-FixtureInstall $fixture $actions -StopWaitAttempts 3 } "Installed host process did not exit: $($fixture.HostPath)" 'bounded stop timeout'
                Assert-Equal 1 $actions.State.StopCalls 'timeout stops once'
                Assert-Equal 3 $actions.State.WaitCalls 'timeout uses bounded polling'
                Assert-Equal 0 $actions.State.CopyCalls 'timeout precedes all copies'
                Assert-Equal 0 $actions.State.RemoveCalls 'timeout preserves installed tree'
                Assert-Equal 0 $actions.State.RegisterCalls 'timeout precedes registration'
                Assert-Equal 0 $actions.State.StartCalls 'timeout precedes startup'
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

$allScenarios = @('static', 'task-plan', 'state-security', 'identity-mismatch', 'identity-binding', 'installer-mutex', 'manifest-install', 'corrupt-manager', 'corrupt-installed-manager', 'stale-cleanup', 'stop-race', 'stop-timeout', 'reinstall-idempotent')
if ($Scenario -eq 'all') { foreach ($name in $allScenarios) { Run-Scenario $name } }
else { Run-Scenario $Scenario }
Write-Host 'Install service host script checks passed.'
