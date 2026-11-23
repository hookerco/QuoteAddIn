[CmdletBinding()]
param(
    [ValidateSet('static', 'qbfc', 'manifest', 'install', 'rollback', 'all')]
    [string]$Scenario = 'all'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'install_service_host.ps1'
$content = Get-Content -Raw -LiteralPath $scriptPath
. $scriptPath -AsLibrary

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) { throw "$Because. Expected [$Expected], got [$Actual]." }
}

function Assert-True([bool]$Condition, [string]$Because) {
    if (-not $Condition) { throw $Because }
}

function Assert-Contains([string]$Pattern, [string]$Because) {
    if ($content -notmatch $Pattern) { throw $Because }
}

function Assert-NotContains([string]$Pattern, [string]$Because) {
    if ($content -match $Pattern) { throw $Because }
}

function New-Fixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('service-host-user-install-' + [guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'share'
    $install = Join-Path $root 'LocalAppData\Programs\QuickBooksServiceHost'
    $state = Join-Path $root 'LocalAppData\QuickBooksServiceHost'
    $desktop = Join-Path $root 'Desktop'
    New-Item -ItemType Directory -Force -Path $source, $desktop, (Join-Path $source 'nested') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'Install-QBUtility.bat'), '@echo off')
    [IO.File]::WriteAllText((Join-Path $source 'QuickBooksServiceHost.exe'), 'host')
    [IO.File]::WriteAllText((Join-Path $source 'QuickBooksConnectorCli.exe'), 'cli')
    [IO.File]::WriteAllText((Join-Path $source 'nested\runtime.dll'), 'runtime')
    [IO.File]::WriteAllText((Join-Path $source 'service_host_manager.ps1'), '# manager')
    [IO.File]::WriteAllText((Join-Path $source 'bridge.settings.psd1'), "@{`nQB_BRIDGE_TOKEN='invented-token'`nQB_BRIDGE_ORIGIN='http://invented.invalid:8742'`nQB_BRIDGE_PORT='8788'`nQB_QUOTE_SUBTOTAL_ITEM='Invented Subtotal'`nQB_QUOTE_DISCOUNT_ITEM='Invented Discount'`n}")
    $files = foreach ($relative in @('QuickBooksServiceHost.exe', 'QuickBooksConnectorCli.exe', 'nested/runtime.dll')) {
        $path = Join-Path $source $relative
        [pscustomobject]@{ path = $relative; length = [long](Get-Item $path).Length; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash }
    }
    $manager = Join-Path $source 'service_host_manager.ps1'
    [pscustomobject]@{
        schema_version = 1
        release_id = 'invented-release'
        published_at_utc = '2026-08-10T12:00:00Z'
        files = @($files)
        manager = [pscustomobject]@{ path = 'service_host_manager.ps1'; length = [long](Get-Item $manager).Length; sha256 = (Get-FileHash $manager -Algorithm SHA256).Hash }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $source 'release.manifest.json')
    [pscustomobject]@{ Root=$root; Source=$source; Install=$install; State=$state; Desktop=$desktop }
}

function New-Actions($Fixture) {
    $state = [pscustomobject]@{ Environment=@{}; Shortcuts=@(); Processes=@(); Stops=0; Starts=0; Events=@(); IntranetCalls=0 }
    [pscustomobject]@{
        State = $state
        SetEnvironment = { param($name,$value) $state.Environment[$name]=[string]$value }.GetNewClosure()
        ConfigureIntranet = { $state.IntranetCalls++ }.GetNewClosure()
        CreateShortcut = {
            param($path,$target,$working,$arguments)
            $state.Shortcuts += [pscustomobject]@{Path=$path;Target=$target;Working=$working;Arguments=$arguments}
        }.GetNewClosure()
        GetProcesses = { @($state.Processes) }.GetNewClosure()
        StopProcess = { param($process) $state.Stops++; $state.Processes=@($state.Processes | Where-Object Id -ne $process.Id) }.GetNewClosure()
        Delay = { param($milliseconds) }.GetNewClosure()
        Move = { param($source,$destination) New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null; Move-Item -LiteralPath $source -Destination $destination -Force }.GetNewClosure()
        Copy = { param($source,$destination) New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null; Copy-Item -LiteralPath $source -Destination $destination -Force }.GetNewClosure()
        Remove = { param($path,$recurse) Remove-Item -LiteralPath $path -Force -Recurse:$recurse -ErrorAction SilentlyContinue }.GetNewClosure()
        Mutex = { param($name,$action) $state.Events += $name; & $action }.GetNewClosure()
    }
}

function Invoke-Fixture($Fixture,$Actions) {
    Invoke-ServiceHostInstall -SourcePath $Fixture.Source -InstallPath $Fixture.Install -StatePath $Fixture.State `
        -CurrentUserDesktopPath $Fixture.Desktop -ReadEnvironmentVariableAction { param($name) $Actions.State.Environment[$name] } -SetEnvironmentVariableAction $Actions.SetEnvironment -ConfigureIntranetAction $Actions.ConfigureIntranet `
        -CreateShortcutAction $Actions.CreateShortcut -GetProcessesAction $Actions.GetProcesses `
        -StopProcessAction $Actions.StopProcess -DelayAction $Actions.Delay -MovePathAction $Actions.Move `
        -CopyFileAction $Actions.Copy -RemovePathAction $Actions.Remove -MutexAction $Actions.Mutex
}

function Run-Scenario([string]$Name) {
    switch ($Name) {
        'static' {
            Assert-NotContains 'Register-ScheduledTask|New-ScheduledTask|Start-ScheduledTask|Unregister-ScheduledTask' 'ordinary installer must not contain scheduled-task operations'
            Assert-NotContains 'Test-IsAdministrator|EnvironmentVariableTarget\]::Machine|CommonDesktopDirectory|ProgramData|ProgramFiles' 'ordinary installer must remain current-user scoped'
            Assert-NotContains 'Start-Process\s+-FilePath\s+\$hostPath' 'ordinary installer must never launch the host'
            Assert-Contains 'QBFC14_0installer\.exe' 'installer must include the bundled QBFC prerequisite'
            Assert-Contains 'Start-Process[\s\S]*-Verb\s+RunAs' 'missing QBFC prerequisite must request administrator permission'
            Assert-Contains "EnvironmentVariableTarget\]::User" 'environment variables must be user-level'
            Assert-Contains "LocalApplicationData[\s\S]*Programs[\s\S]*QuickBooksServiceHost" 'runtime must default below LocalAppData Programs'
            Assert-Contains "LocalApplicationData[\s\S]*QuickBooksServiceHost" 'state must default below LocalAppData'
            Assert-Contains "GetFolderPath\('DesktopDirectory'\)" 'shortcut must use the current user desktop'
        }
        'qbfc' {
            $root = Join-Path ([IO.Path]::GetTempPath()) ('qbfc-install-' + [guid]::NewGuid().ToString('N'))
            $source = Join-Path $root 'service_host_installation'
            New-Item -ItemType Directory -Force -Path $source | Out-Null
            [IO.File]::WriteAllText((Join-Path $root 'QBFC14_0installer.exe'),'fixture')
            try {
                $state = [pscustomobject]@{ Registered=$false; Installs=0; Path='' }
                $result = Ensure-QbfcPrerequisite -SourcePath $source `
                    -TestRegisteredAction { $state.Registered }.GetNewClosure() `
                    -InstallAction {
                        param($path)
                        $state.Installs++
                        $state.Path=$path
                        $state.Registered=$true
                        return 0
                    }.GetNewClosure()
                Assert-Equal 'Installed' $result 'missing QBFC is installed'
                Assert-Equal 1 $state.Installs 'QBFC installer runs once'
                Assert-Equal (Join-Path $root 'QBFC14_0installer.exe') $state.Path 'bundled QBFC installer is used'
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
        'manifest' {
            foreach ($unsafe in @('..\outside.dll','.\runtime.dll','C:\outside.dll','nested//runtime.dll','nested:stream')) {
                Assert-True (-not (Test-SafeReleasePath $unsafe)) "unsafe manifest path is rejected: $unsafe"
            }
            Assert-True (Test-SafeReleasePath 'nested/runtime.dll') 'normal nested manifest path is accepted'
            $manifest=[pscustomobject]@{schema_version=1;release_id='invented';files=@(
                [pscustomobject]@{path='QuickBooksServiceHost.exe';length=1;sha256=('A'*64)},
                [pscustomobject]@{path='QuickBooksConnectorCli.exe';length=1;sha256=('B'*64)},
                [pscustomobject]@{path='quickbooksconnectorcli.EXE';length=1;sha256=('C'*64)}
            );manager=[pscustomobject]@{path='service_host_manager.ps1';length=1;sha256=('D'*64)}}
            $caught=$null;try{Test-ServiceHostReleaseManifest $manifest}catch{$caught=$_}
            Assert-True ($caught.Exception.Message -match 'Duplicate') 'case-insensitive duplicate manifest entries are rejected'
        }
        'install' {
            $zoneWrite=@{}
            Set-CompanyIntranetSite -WriteZoneAction { param($path,$protocol,$zone) $zoneWrite.Path=$path; $zoneWrite.Protocol=$protocol; $zoneWrite.Zone=$zone }
            Assert-Equal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Domains\proto-cam.local\act' $zoneWrite.Path 'only the specific current-user hostname is configured'
            Assert-Equal 'https' $zoneWrite.Protocol 'only HTTPS is configured'
            Assert-Equal 1 $zoneWrite.Zone 'hostname uses Local Intranet zone'
            foreach ($legacy in @('http://APPSRV01:8742','http://PC-VS-APPFS01:8742/',
                'http://pc-vs-appfs01.proto-cam.local:8742','http://act.proto-cam.local:8742')) {
                Assert-Equal 'https://act.proto-cam.local:8743' (Resolve-ServiceHostBridgeOrigin $legacy) 'known company origins migrate'
            }
            Assert-Equal 'https://custom.invalid:9999' (Resolve-ServiceHostBridgeOrigin 'https://custom.invalid:9999') 'custom origin preserved'
            $launchState = @{ Settings=@{}; Path='' }
            Start-CurrentUserBridge -HostPath 'C:\invented\host.exe' `
                -ReadUserSetting { param($name) if($name -eq 'QB_BRIDGE_ORIGIN'){'https://act.proto-cam.local:8743'}elseif($name -eq 'QB_BRIDGE_TOKEN'){'invented-kept-token'}elseif($name -eq 'QB_QUOTE_SUBTOTAL_ITEM'){'Invented Subtotal'}elseif($name -eq 'QB_QUOTE_DISCOUNT_ITEM'){'Invented Discount'} } `
                -SetProcessSetting { param($name,$value) $launchState.Settings[$name]=$value } `
                -StartAction { param($path) $launchState.Path=$path }
            Assert-Equal 'https://act.proto-cam.local:8743' $launchState.Settings.QB_BRIDGE_ORIGIN 'launcher refreshes origin before starting'
            Assert-Equal 'Invented Subtotal' $launchState.Settings.QB_QUOTE_SUBTOTAL_ITEM 'launcher refreshes subtotal mapping'
            Assert-Equal 'Invented Discount' $launchState.Settings.QB_QUOTE_DISCOUNT_ITEM 'launcher refreshes discount mapping'
            Assert-Equal 'invented-kept-token' $launchState.Settings.QB_BRIDGE_TOKEN 'launcher preserves current user token'
            Assert-Equal 'C:\invented\host.exe' $launchState.Path 'launcher starts selected host'
            $defaultStart = ((Get-Command Start-CurrentUserBridge).ScriptBlock.Ast.Body.ParamBlock.Parameters |
                Where-Object { $_.Name.VariablePath.UserPath -eq 'StartAction' }).DefaultValue.Extent.Text
            Assert-True ($defaultStart -match '-WindowStyle Normal') 'desktop shortcut shows the host console window'
            $fixture = New-Fixture
            try {
                $actions = New-Actions $fixture
                $result = Invoke-Fixture $fixture $actions
                Assert-Equal 'Installed' $result.Phase 'install completes without starting host'
                Assert-Equal 'host' ([IO.File]::ReadAllText((Join-Path $fixture.Install 'QuickBooksServiceHost.exe'))) 'host is deployed'
                Assert-Equal 'cli' ([IO.File]::ReadAllText((Join-Path $fixture.Install 'QuickBooksConnectorCli.exe'))) 'connector CLI is deployed'
                Assert-Equal '# manager' ([IO.File]::ReadAllText((Join-Path $fixture.State 'Manager\service_host_manager.ps1'))) 'manager package is preserved'
                Assert-Equal 'invented-release' ((Get-Content -Raw (Join-Path $fixture.State 'release.manifest.json') | ConvertFrom-Json).release_id) 'manifest is installed'
                Assert-Equal (Join-Path $fixture.Install 'QuickBooksConnectorCli.exe') $actions.State.Environment.QUOTE_MODULEV2_QB_CONNECTOR_CLI 'connector variable is user scoped through installer action'
                Assert-Equal 'Invented Subtotal' $actions.State.Environment.QB_QUOTE_SUBTOTAL_ITEM 'subtotal mapping persists for user'
                Assert-Equal 'Invented Discount' $actions.State.Environment.QB_QUOTE_DISCOUNT_ITEM 'discount mapping persists for user'
                Assert-Equal 'invented-token' $actions.State.Environment.QB_BRIDGE_TOKEN 'bridge token behavior is preserved'
                Assert-Equal 'http://invented.invalid:8742' $actions.State.Environment.QB_BRIDGE_ORIGIN 'bridge origin behavior is preserved'
                Assert-Equal 0 $actions.State.IntranetCalls 'custom origins do not change company intranet settings'
                $hostShortcut = @($actions.State.Shortcuts | Where-Object Path -eq (Join-Path $fixture.Desktop 'QuickBooksServiceHost.lnk'))
                $updaterShortcut = @($actions.State.Shortcuts | Where-Object Path -eq (Join-Path $fixture.Desktop 'Update QB Utility.lnk'))
                Assert-Equal 1 $hostShortcut.Count 'host shortcut is installed once for the current user'
                Assert-True ($hostShortcut[0].Target -like '*powershell.exe') 'shortcut refreshes settings through local launcher'
                Assert-True ($hostShortcut[0].Arguments -match 'LaunchInstalledHost') 'shortcut selects launch mode'
                Assert-True (Test-Path (Join-Path $fixture.State 'launch_service_host.ps1')) 'local launcher is installed'
                Assert-Equal $fixture.Install $hostShortcut[0].Working 'host shortcut uses the installed runtime as its working directory'
                Assert-Equal 1 $updaterShortcut.Count 'updater shortcut is installed once for the current user'
                Assert-Equal (Join-Path $fixture.Root 'Install-QBUtility.bat') $updaterShortcut[0].Target 'updater shortcut launches the complete utility installer'
                Assert-Equal $fixture.Root $updaterShortcut[0].Working 'updater shortcut uses the shared installer directory'
                Assert-Equal '' $updaterShortcut[0].Arguments 'utility installer shortcut needs no extra arguments'
                Assert-Equal (Join-Path $fixture.Desktop 'Update QB Utility.lnk') $result.UpdaterShortcutPath 'result reports the updater shortcut path'
                Assert-Equal 0 $actions.State.Starts 'host is not launched'
                Assert-Equal 0 @($actions.State.Processes).Count 'host remains stopped after installation'
                Assert-Equal 'Local\QuickBooksServiceHostInstall' $actions.State.Events[0] 'installer uses a per-session mutex'
                $settings=Join-Path $fixture.Source 'bridge.settings.psd1'
                $oldSettings=[IO.File]::ReadAllText($settings)
                [IO.File]::WriteAllText($settings,$oldSettings.Replace('http://invented.invalid:8742','http://pc-vs-appfs01:8742'))
                $actions=New-Actions $fixture
                Invoke-Fixture $fixture $actions | Out-Null
                Assert-Equal 'https://act.proto-cam.local:8743' $actions.State.Environment.QB_BRIDGE_ORIGIN 'update replaces old user origin'
                Assert-Equal 'invented-token' $actions.State.Environment.QB_BRIDGE_TOKEN 'migration retains package token'
                Assert-Equal 1 $actions.State.IntranetCalls 'company updater configures the current user intranet entry'
                Invoke-Fixture $fixture $actions | Out-Null
                Assert-Equal 2 $actions.State.IntranetCalls 'repeated updates reapply the same intranet setting safely'
                $tokenFile = Join-Path $fixture.Root 'sample-token.txt'
                [IO.File]::WriteAllText($tokenFile,'invented-conversion-secret')
                $site = @{
                    QB_BRIDGE_ORIGIN='https://example.invalid:8743'
                    QB_OPPORTUNITY_COMPUTER=$env:COMPUTERNAME
                    QB_OPPORTUNITY_COMPANY='C:\Invented\Sales.qbw'
                    QB_OPPORTUNITY_SINCE='2026-11-23'
                    QB_OPPORTUNITY_TOKEN_FILE=$tokenFile
                }
                $saveSite = {
                    $lines = @($site.GetEnumerator() | ForEach-Object { $_.Key + "='" + $_.Value.Replace("'","''") + "'" })
                    [IO.File]::WriteAllText($settings,"@{" + [Environment]::NewLine + ($lines -join [Environment]::NewLine) + [Environment]::NewLine + "}")
                }
                & $saveSite
                Invoke-Fixture $fixture $actions | Out-Null
                Assert-Equal $site.QB_OPPORTUNITY_COMPANY $actions.State.Environment.QB_OPPORTUNITY_COMPANY 'designated seat enables the watcher'
                Assert-Equal $site.QB_BRIDGE_ORIGIN $actions.State.Environment.QB_OPPORTUNITY_WORKBENCH 'watcher uses shared origin'
                Assert-Equal 'invented-conversion-secret' $actions.State.Environment.QUOTE_MODULEV2_QB_CONVERSION_TOKEN 'designated seat receives conversion token'
                Assert-Equal $site.QB_OPPORTUNITY_SINCE $actions.State.Environment.QB_OPPORTUNITY_SINCE 'installer sets the scan date'
                $site.QB_OPPORTUNITY_COMPUTER='*'
                & $saveSite
                Invoke-Fixture $fixture $actions | Out-Null
                Assert-Equal $site.QB_OPPORTUNITY_COMPANY $actions.State.Environment.QB_OPPORTUNITY_COMPANY 'all-seat mode installs the same pinned company'
                $site.QB_OPPORTUNITY_COMPUTER='INVENTED-OTHER'
                $site.QB_OPPORTUNITY_TOKEN_FILE=Join-Path $fixture.Root 'does-not-exist'
                & $saveSite
                Invoke-Fixture $fixture $actions | Out-Null
                Assert-Equal '' $actions.State.Environment.QB_OPPORTUNITY_COMPANY 'reassigned seat clears company'
                Assert-Equal '' $actions.State.Environment.QUOTE_MODULEV2_QB_CONVERSION_TOKEN 'ordinary seat skips secret file and clears old token'
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }

            $fixture = New-Fixture
            try {
                Remove-Item -LiteralPath (Join-Path $fixture.Root 'Install-QBUtility.bat') -Force
                $caught=$null;try{Invoke-Fixture $fixture (New-Actions $fixture)}catch{$caught=$_}
                Assert-True ($caught.Exception.Message -match 'Install-QBUtility\.bat') 'missing utility installer fails before creating a broken updater shortcut'
                Assert-True (-not (Test-Path -LiteralPath $fixture.Install)) 'missing utility installer does not mutate the runtime'
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
        }
        'rollback' {
            $fixture = New-Fixture
            try {
                New-Item -ItemType Directory -Force -Path $fixture.Install, (Join-Path $fixture.State 'Manager') | Out-Null
                [IO.File]::WriteAllText((Join-Path $fixture.Install 'QuickBooksServiceHost.exe'),'old-host')
                [IO.File]::WriteAllText((Join-Path $fixture.Install 'QuickBooksConnectorCli.exe'),'old-cli')
                [IO.File]::WriteAllText((Join-Path $fixture.State 'Manager\service_host_manager.ps1'),'# old manager')
                [IO.File]::WriteAllText((Join-Path $fixture.State 'release.manifest.json'),'{"release_id":"old-release"}')
                $actions = New-Actions $fixture
                $originalMove = $actions.Move
                $failed = [pscustomobject]@{ Value=$false }
                $actions.Move = {
                    param($source,$destination)
                    if (-not $failed.Value -and $destination -ieq (Join-Path $fixture.State 'Manager\service_host_manager.ps1')) { $failed.Value=$true; throw 'invented promotion failure' }
                    & $originalMove $source $destination
                }.GetNewClosure()
                $caught = $null
                try { Invoke-Fixture $fixture $actions } catch { $caught=$_ }
                Assert-Equal 'invented promotion failure' $caught.Exception.Message 'promotion failure remains primary'
                Assert-Equal 'old-host' ([IO.File]::ReadAllText((Join-Path $fixture.Install 'QuickBooksServiceHost.exe'))) 'rollback restores old runtime'
                Assert-Equal '# old manager' ([IO.File]::ReadAllText((Join-Path $fixture.State 'Manager\service_host_manager.ps1'))) 'rollback restores old manager'
                Assert-Equal 0 $actions.State.Starts 'rollback does not restart the old host'
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }

            $fixture = New-Fixture
            try {
                New-Item -ItemType Directory -Force -Path $fixture.Install, (Join-Path $fixture.State 'Manager') | Out-Null
                [IO.File]::WriteAllText((Join-Path $fixture.Install 'QuickBooksServiceHost.exe'),'old-host')
                [IO.File]::WriteAllText((Join-Path $fixture.State 'Manager\service_host_manager.ps1'),'# untouched manager')
                [IO.File]::WriteAllText((Join-Path $fixture.State 'release.manifest.json'),'{"release_id":"untouched-release"}')
                $actions = New-Actions $fixture
                $actions.State.Processes=@([pscustomobject]@{Id=9;ProcessName='QuickBooksServiceHost';Path='C:\Legacy\QuickBooksServiceHost.exe'})
                $caught=$null
                try { Invoke-Fixture $fixture $actions } catch { $caught=$_ }
                Assert-True ($null -ne $caught -and $caught.Exception.Message -match 'migrate_legacy_service_host') "foreign legacy host blocks the user install, got: $($caught.Exception.Message)"
                Assert-Equal '# untouched manager' ([IO.File]::ReadAllText((Join-Path $fixture.State 'Manager\service_host_manager.ps1'))) 'preflight failure preserves installed manager'
                Assert-Equal 'untouched-release' ((Get-Content -Raw (Join-Path $fixture.State 'release.manifest.json') | ConvertFrom-Json).release_id) 'preflight failure preserves installed manifest'
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

$scenarios = if ($Scenario -eq 'all') { @('static','qbfc','manifest','install','rollback') } else { @($Scenario) }
foreach ($name in $scenarios) { Run-Scenario $name; Write-Output "PASS: $name" }
