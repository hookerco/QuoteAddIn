[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Scenario)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'service_host_manager.ps1') -AsLibrary

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) {
        throw "$Because. Expected [$Expected], got [$Actual]."
    }
}

function Assert-True([bool]$Value, [string]$Because) {
    if (-not $Value) { throw $Because }
}

function Assert-Throws([scriptblock]$Action, [string]$Because) {
    $threw = $false
    try {
        & $Action
    }
    catch {
        $threw = $true
    }
    if (-not $threw) { throw $Because }
}

function Assert-ThrowsMessage([scriptblock]$Action, [string]$ExpectedMessage, [string]$Because) {
    try {
        & $Action
    }
    catch {
        Assert-Equal $ExpectedMessage $_.Exception.Message $Because
        return
    }
    throw $Because
}

function New-ValidManifest {
    return [pscustomobject]@{
        schema_version = 1
        release_id = 'release-b'
        published_at_utc = '2026-07-15T10:00:00Z'
        files = @(
            [pscustomobject]@{
                path = 'QuickBooksServiceHost.exe'
                length = 123
                sha256 = ('A' * 64)
            }
        )
        manager = [pscustomobject]@{
            path = 'service_host_manager.ps1'
            length = 456
            sha256 = ('B' * 64)
        }
    }
}

function New-TestTree {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('service-host-manager-test-' + [guid]::NewGuid().ToString('N'))
    $share = Join-Path $root 'share'
    $install = Join-Path $root 'install'
    $statePath = Join-Path $root 'state'
    New-Item -ItemType Directory -Force -Path $share, $install, $statePath | Out-Null
    [pscustomobject]@{
        Root = $root
        Share = $share
        Install = $install
        StatePath = $statePath
    }
}

function Remove-TestTree($Tree) {
    Remove-Item -LiteralPath $Tree.Root -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-TestRelease {
    param(
        $Tree,
        [string]$ReleaseId = 'release-b',
        [string]$HostBytes = 'new-bytes',
        [string]$ManagerBytes = 'new-manager'
    )

    $hostPath = Join-Path $Tree.Share 'QuickBooksServiceHost.exe'
    $managerPath = Join-Path $Tree.Share 'service_host_manager.ps1'
    [IO.File]::WriteAllText($hostPath, $HostBytes)
    [IO.File]::WriteAllText($managerPath, $ManagerBytes)
    $manifest = [pscustomobject]@{
        schema_version = 1
        release_id = $ReleaseId
        published_at_utc = '2026-07-15T10:00:00Z'
        files = @(
            [pscustomobject]@{
                path = 'QuickBooksServiceHost.exe'
                length = [long](Get-Item -LiteralPath $hostPath).Length
                sha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash
            }
        )
        manager = [pscustomobject]@{
            path = 'service_host_manager.ps1'
            length = [long](Get-Item -LiteralPath $managerPath).Length
            sha256 = (Get-FileHash -LiteralPath $managerPath -Algorithm SHA256).Hash
        }
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Tree.Share 'release.manifest.json')
    return $manifest
}

function Write-InstalledRelease {
    param($Tree, [string]$ReleaseId = 'release-a', [string]$HostBytes = 'old-bytes')

    [IO.File]::WriteAllText((Join-Path $Tree.Install 'QuickBooksServiceHost.exe'), $HostBytes)
    [IO.File]::WriteAllText((Join-Path $Tree.StatePath 'service_host_manager.ps1'), 'old-manager')
    [pscustomobject]@{
        schema_version = 1
        release_id = $ReleaseId
        published_at_utc = '2026-07-14T10:00:00Z'
        files = @()
        manager = $null
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Tree.StatePath 'installed.manifest.json')
}

function New-OrchestrationActions {
    param($State, [object[]]$Processes = @())

    [pscustomobject]@{
        GetProcesses = { $Processes }.GetNewClosure()
        StopProcess = { param($process) $State.Stopped += @($process.Id) }.GetNewClosure()
        StartHost = { $State.Starts++ }.GetNewClosure()
        TestHost = { $true }
        Mutex = { param($name, $action) $State.MutexName = $name; & $action }.GetNewClosure()
    }
}

function Run-Scenario {
    param([Parameter(Mandatory = $true)][string]$Name)

    switch ($Name) {
        'decision-offline-host-missing' {
            Assert-Equal 'start-installed' (Get-ManagerDecision -ShareAvailable $false `
                -ManifestValid $false -InstalledReleaseId 'a' -AvailableReleaseId '' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $false) `
                'offline startup must use installed payload'
        }
        'decision-current-host-running' {
            Assert-Equal 'keep-running' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId 'a' -AvailableReleaseId 'a' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $true) `
                'current running host must remain untouched'
        }
        'decision-current-host-missing' {
            Assert-Equal 'start-installed' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId 'a' -AvailableReleaseId 'a' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $false) `
                'current missing host must start the installed payload'
        }
        'decision-invalid-manifest-host-missing' {
            Assert-Equal 'start-installed' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $false -InstalledReleaseId 'a' -AvailableReleaseId 'b' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $false) `
                'an invalid available manifest must fall back to the installed payload'
        }
        'decision-update-idle' {
            Assert-Equal 'apply-update' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId 'a' -AvailableReleaseId 'b' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $true) `
                'idle workstation must apply a complete newer release'
        }
        'decision-update-qb-open' {
            Assert-Equal 'defer-update' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId 'a' -AvailableReleaseId 'b' `
                -QuickBooksRunning $true -ConnectorRunning $false -HostRunning $true) `
                'open QuickBooks must defer replacement'
        }
        'decision-update-connector-running' {
            Assert-Equal 'defer-update' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId 'a' -AvailableReleaseId 'b' `
                -QuickBooksRunning $false -ConnectorRunning $true -HostRunning $true) `
                'an active connector must defer replacement'
        }
        'decision-no-installed-manifest' {
            Assert-Equal 'apply-update' (Get-ManagerDecision -ShareAvailable $true `
                -ManifestValid $true -InstalledReleaseId '' -AvailableReleaseId 'b' `
                -QuickBooksRunning $false -ConnectorRunning $false -HostRunning $false) `
                'a workstation without an installed release must install an available release'
        }
        'manifest-reads-valid' {
            $path = [IO.Path]::GetTempFileName()
            try {
                New-ValidManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
                $manifest = Read-ReleaseManifest -Path $path
                Assert-Equal 'release-b' $manifest.release_id 'valid manifest must be read and returned'
                Assert-Equal $true (Test-ReleaseManifest -Manifest $manifest) `
                    'valid manifest must pass validation'
            }
            finally {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
        }
        'manifest-rejects-traversal' {
            $manifest = New-ValidManifest
            $manifest.files[0].path = '..\evil.exe'
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Unsafe manifest path: ..\evil.exe' `
                'manifest traversal path must be rejected precisely'
        }
        'manifest-rejects-schema' {
            $manifest = New-ValidManifest
            $manifest.schema_version = 2
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Unsupported release manifest schema.' `
                'unsupported manifest schema must be rejected precisely'
        }
        'manifest-rejects-release-id' {
            $manifest = New-ValidManifest
            $manifest.release_id = ' '
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Release ID is required.' `
                'missing manifest release ID must be rejected precisely'
        }
        'manifest-rejects-duplicate' {
            $manifest = New-ValidManifest
            $manifest.manager.path = 'quickbooksservicehost.EXE'
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Duplicate manifest path: quickbooksservicehost.EXE' `
                'duplicate manifest paths must be rejected case-insensitively'
        }
        'manifest-rejects-length' {
            $manifest = New-ValidManifest
            $manifest.files[0].length = -1
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Invalid manifest length: QuickBooksServiceHost.exe' `
                'negative manifest length must be rejected precisely'
        }
        'manifest-rejects-hash' {
            $manifest = New-ValidManifest
            $manifest.files[0].sha256 = ('G' * 64)
            Assert-ThrowsMessage { Test-ReleaseManifest -Manifest $manifest } `
                'Invalid manifest hash: QuickBooksServiceHost.exe' `
                'non-hex SHA-256 must be rejected precisely'
        }
        'update-success' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Stops = 0; Starts = 0; Healthy = $true }
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                $health = { $state.Healthy }.GetNewClosure()
                Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                    -InstallPath $tree.Install -StatePath $tree.StatePath `
                    -StopHost $stop -StartHost $start -TestHost $health
                Assert-Equal 1 $state.Stops 'host stopped once after staging verifies'
                Assert-Equal 1 $state.Starts 'new host started once'
                Assert-Equal 'new-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'new runtime installed'
                Assert-Equal 'release-b' ((Get-Content -Raw (Join-Path $tree.StatePath 'installed.manifest.json') | ConvertFrom-Json).release_id) `
                    'installed release state updated'
            }
            finally { Remove-TestTree $tree }
        }
        'update-rolls-back' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Stops = 0; Starts = 0; Healthy = $false }
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = {
                    $state.Starts++
                    if ($state.Starts -eq 2) { $state.Healthy = $true }
                }.GetNewClosure()
                $health = { $state.Healthy }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost $health
                } 'Release failed; previous host restored.' `
                    'failed replacement must report failure after restoring the previous host'
                Assert-Equal 1 $state.Stops 'rollback transaction stops the old host once'
                Assert-Equal 2 $state.Starts 'rollback transaction starts new then previous host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'failed replacement restores old runtime bytes'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'installed.manifest.json') | ConvertFrom-Json).release_id) `
                    'failed replacement restores old installed manifest'
                Assert-Equal 'old-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'service_host_manager.ps1')) `
                    'runtime transaction leaves manager reconciliation to orchestration'
            }
            finally { Remove-TestTree $tree }
        }
        'stage-hash-fails-before-stop' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $manifest.files[0].sha256 = ('C' * 64)
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                Assert-Throws {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost { $true }
                } 'corrupt staged bytes must reject the release'
                Assert-Equal 0 $state.Stops 'staging verification failure must not stop host'
                Assert-Equal 0 $state.Starts 'staging verification failure must not start host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'staging verification failure preserves installed bytes'
            }
            finally { Remove-TestTree $tree }
        }
        'manager-self-updates' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree -ManagerBytes 'new-manager' | Out-Null
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @([pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 51; Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe') })
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 'new-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'service_host_manager.ps1')) `
                    'verified manager entry replaces local manager after runtime result is final'
            }
            finally { Remove-TestTree $tree }
        }
        'hidden-startup' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                $capture = [pscustomobject]@{ FilePath = ''; WorkingDirectory = ''; WindowStyle = '' }
                $process = Start-InstalledHost -InstallPath $tree.Install -StartProcessAction {
                    param($filePath, $workingDirectory, $windowStyle)
                    $capture.FilePath = $filePath
                    $capture.WorkingDirectory = $workingDirectory
                    $capture.WindowStyle = $windowStyle
                    return [pscustomobject]@{ Id = 42; Path = $filePath }
                }.GetNewClosure()
                Assert-Equal (Join-Path $tree.Install 'QuickBooksServiceHost.exe') $capture.FilePath `
                    'host startup uses installed executable'
                Assert-Equal $tree.Install $capture.WorkingDirectory 'host startup uses exact install working directory'
                Assert-Equal 'Hidden' $capture.WindowStyle 'host startup uses hidden window style'
                Assert-Equal 42 $process.Id 'host startup returns created process'
            }
            finally { Remove-TestTree $tree }
        }
        'share-offline-starts-installed' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                Remove-Item -LiteralPath $tree.Share -Recurse -Force
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $actions = New-OrchestrationActions -State $state
                $result = Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex
                Assert-Equal 0 $result 'offline manager invocation exits successfully'
                Assert-Equal 1 $state.Starts 'offline manager starts installed host when missing'
                Assert-Equal 0 $state.Stopped.Count 'offline manager does not stop unrelated processes'
            }
            finally { Remove-TestTree $tree }
        }
        'qb-defers' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                Write-TestRelease -Tree $tree | Out-Null
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QBW32'; Id = 10; Path = 'C:\Fake\QBW32.exe' },
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 11; Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe') }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 0 $state.Starts 'QuickBooks activity defers replacement startup'
                Assert-Equal 0 $state.Stopped.Count 'QuickBooks activity defers before stopping host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'QuickBooks deferral preserves installed bytes'
            }
            finally { Remove-TestTree $tree }
        }
        'cli-defers' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                Write-TestRelease -Tree $tree | Out-Null
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QuickBooksConnectorCli'; Id = 20; Path = 'C:\Fake\QuickBooksConnectorCli.exe' },
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 21; Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe') }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 0 $state.Starts 'connector activity defers replacement startup'
                Assert-Equal 0 $state.Stopped.Count 'connector activity defers before stopping host'
            }
            finally { Remove-TestTree $tree }
        }
        'mutex-skips-overlap' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Starts = 0; MutexName = '' }
                $skipMutex = {
                    param($name, $action)
                    $state.MutexName = $name
                    return 0
                }.GetNewClosure()
                $result = Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction { throw 'must not inspect processes' } `
                    -StopProcessAction { throw 'must not stop processes' } `
                    -StartHost { $state.Starts++ }.GetNewClosure() -TestHost { $true } -MutexAction $skipMutex
                Assert-Equal 0 $result 'overlapping invocation exits successfully'
                Assert-Equal 'Global\QuickBooksServiceHostAutoUpdate' $state.MutexName 'manager uses required mutex name'
                Assert-Equal 0 $state.Starts 'overlapping invocation performs no host action'
                $log = Get-Content -Raw (Join-Path $tree.StatePath 'service-host-manager.log')
                Assert-True ($log -match 'overlap_skipped') 'overlapping invocation records its skipped decision'
            }
            finally { Remove-TestTree $tree }
        }
        'wrong-path-host-replaced' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 31; Path = 'C:\Wrong\QuickBooksServiceHost.exe' },
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 32; Path = 'C:\Duplicate\QuickBooksServiceHost.exe' }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath (Join-Path $tree.Root 'offline') -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 2 $state.Stopped.Count 'all wrong-path host processes are stopped'
                Assert-Equal 1 $state.Starts 'expected installed host starts after wrong-path hosts stop'
            }
            finally { Remove-TestTree $tree }
        }
        'logs-rotate' {
            $tree = New-TestTree
            try {
                $logPath = Join-Path $tree.StatePath 'service-host-manager.log'
                [IO.File]::WriteAllText($logPath, ('x' * (1MB - 10)))
                foreach ($index in 1..5) {
                    [IO.File]::WriteAllText("$logPath.$index", "old-$index")
                }
                Write-ManagerLog -StatePath $tree.StatePath -Record ([pscustomobject]@{
                    installed_release = 'a'; available_release = 'b'; decision = 'apply-update'
                    result = ('y' * 200); rollback = $false; host_pid = 1; host_path = 'C:\fake\host.exe'
                })
                Assert-True (Test-Path -LiteralPath "$logPath.1") 'oversized log rotates to .1'
                Assert-True (Test-Path -LiteralPath "$logPath.5") 'log rotation retains fifth backup'
                Assert-True (-not (Test-Path -LiteralPath "$logPath.6")) 'log rotation never creates sixth backup'
                Assert-True ((Get-Item -LiteralPath $logPath).Length -lt 1MB) 'active log remains bounded after rotation'
            }
            finally { Remove-TestTree $tree }
        }
        'logs-redact' {
            $tree = New-TestTree
            try {
                $secret = 'super-secret-bridge-token'
                $env:QB_BRIDGE_TOKEN = $secret
                Write-ManagerLog -StatePath $tree.StatePath -Record ([pscustomobject]@{
                    installed_release = 'a'; available_release = 'b'; decision = 'defer-update'
                    result = 'safe'; rollback = $false; host_pid = 1; host_path = 'C:\fake\host.exe'
                    exception = "request used $secret"; environment = @{ QB_BRIDGE_TOKEN = $secret }
                })
                $line = Get-Content -Raw (Join-Path $tree.StatePath 'service-host-manager.log')
                Assert-True (-not $line.Contains($secret)) 'structured log excludes token-bearing exception and environment data'
                $record = $line | ConvertFrom-Json
                Assert-True ($null -ne $record.timestamp_utc) 'structured log includes UTC timestamp'
                Assert-Equal 'defer-update' $record.decision 'structured log preserves allowlisted decision'
            }
            finally {
                Remove-Item Env:\QB_BRIDGE_TOKEN -ErrorAction SilentlyContinue
                Remove-TestTree $tree
            }
        }
        'previous-cleanup-failure-preserves-install' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $previousPath = Join-Path $tree.StatePath 'Previous'
                New-Item -ItemType Directory -Force -Path $previousPath | Out-Null
                [IO.File]::WriteAllText((Join-Path $previousPath 'QuickBooksServiceHost.exe'), 'stale-previous')
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $removeAction = {
                    param($path, [bool]$recurse)
                    if ($path -ieq $previousPath) { throw 'simulated previous cleanup failure' }
                    Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction SilentlyContinue
                }.GetNewClosure()
                $moveAction = { param($source, $destination) Move-Item -LiteralPath $source -Destination $destination }.GetNewClosure()
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost { $true } `
                        -RemovePathAction $removeAction -MovePathAction $moveAction
                } 'simulated previous cleanup failure' 'pre-promotion cleanup failure must remain the reported cause'
                Assert-Equal 1 $state.Stops 'cleanup failure occurs after one completed stop'
                Assert-Equal 1 $state.Starts 'cleanup failure restarts the untouched installed host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'cleanup failure must not delete the still-good install'
                Assert-Equal 'stale-previous' (Get-Content -Raw (Join-Path $previousPath 'QuickBooksServiceHost.exe')) `
                    'cleanup failure must not restore stale Previous over the install'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'installed.manifest.json') | ConvertFrom-Json).release_id) `
                    'cleanup failure must preserve installed manifest'
            }
            finally { Remove-TestTree $tree }
        }
        'move-current-failure-preserves-install' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $previousPath = Join-Path $tree.StatePath 'Previous'
                New-Item -ItemType Directory -Force -Path $previousPath | Out-Null
                [IO.File]::WriteAllText((Join-Path $previousPath 'QuickBooksServiceHost.exe'), 'stale-previous')
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $removeAction = {
                    param($path, [bool]$recurse)
                    Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction SilentlyContinue
                }
                $moveAction = {
                    param($source, $destination)
                    if ($source -ieq $tree.Install) { throw 'simulated current install move failure' }
                    Move-Item -LiteralPath $source -Destination $destination
                }.GetNewClosure()
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost { $true } `
                        -RemovePathAction $removeAction -MovePathAction $moveAction
                } 'simulated current install move failure' 'current move failure must remain the reported cause'
                Assert-Equal 1 $state.Stops 'current move failure occurs after one completed stop'
                Assert-Equal 1 $state.Starts 'current move failure restarts the untouched installed host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'current move failure must not delete the still-good install'
                Assert-True (-not (Test-Path -LiteralPath $previousPath)) `
                    'current move failure must not restore a stale Previous tree after cleanup'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'installed.manifest.json') | ConvertFrom-Json).release_id) `
                    'current move failure must preserve installed manifest'
            }
            finally { Remove-TestTree $tree }
        }
        'partial-stop-failure-restarts-installed' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $stop = {
                    foreach ($hostId in @(1, 2)) {
                        $state.Stops++
                        if ($hostId -eq 2) { throw 'simulated second host stop failure' }
                    }
                }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost { $true }
                } 'simulated second host stop failure' 'partial multi-host stop failure remains the reported cause'
                Assert-Equal 2 $state.Stops 'stop action reached the later host before failing'
                Assert-Equal 1 $state.Starts 'partial stop failure restarts the installed host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'partial stop failure preserves installed runtime'
            }
            finally { Remove-TestTree $tree }
        }
        'activity-race-defers-after-staging' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree | Out-Null
                Write-InstalledRelease -Tree $tree -ReleaseId 'release-a'
                $expectedHost = [pscustomobject]@{
                    ProcessName = 'QuickBooksServiceHost'
                    Id = 62
                    Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe')
                }
                $quickBooks = [pscustomobject]@{ ProcessName = 'QBW32'; Id = 63; Path = 'C:\QuickBooks\QBW32.exe' }
                $state = [pscustomobject]@{ Calls = 0; Stops = 0; Starts = 0; MutexName = '' }
                $getProcesses = {
                    $state.Calls++
                    if ($state.Calls -eq 1) { return @($expectedHost) }
                    return @($expectedHost, $quickBooks)
                }.GetNewClosure()
                $stopProcess = { param($process) $state.Stops++ }.GetNewClosure()
                $startHost = { $state.Starts++ }.GetNewClosure()
                $mutex = {
                    param($name, $action)
                    $state.MutexName = $name
                    & $action
                }.GetNewClosure()

                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $getProcesses `
                    -StopProcessAction $stopProcess -StartHost $startHost -TestHost { $true } `
                    -MutexAction $mutex | Out-Null

                Assert-True ($state.Calls -ge 2) 'activity must be checked again after staging'
                Assert-Equal 0 $state.Stops 'new QuickBooks activity defers before stopping the host'
                Assert-Equal 0 $state.Starts 'deferred activity race does not restart the host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'activity race preserves installed runtime'
            }
            finally { Remove-TestTree $tree }
        }
        'logs-refresh-host-after-action' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree -ReleaseId 'release-a'
                Remove-Item -LiteralPath $tree.Share -Recurse -Force
                $state = [pscustomobject]@{ Calls = 0; Started = $false; MutexName = '' }
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $hostProcess = [pscustomobject]@{
                    ProcessName = 'QuickBooksServiceHost'
                    Id = 71
                    Path = $expectedPath
                }
                $getProcesses = {
                    $state.Calls++
                    if ($state.Started) { return @($hostProcess) }
                    return @()
                }.GetNewClosure()
                $startHost = { $state.Started = $true }.GetNewClosure()
                $mutex = {
                    param($name, $action)
                    $state.MutexName = $name
                    & $action
                }.GetNewClosure()

                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $getProcesses `
                    -StopProcessAction { param($process) } -StartHost $startHost -TestHost { $true } `
                    -MutexAction $mutex | Out-Null

                $record = Get-Content -Raw (Join-Path $tree.StatePath 'service-host-manager.log') | ConvertFrom-Json
                Assert-True ($state.Calls -ge 2) 'logging refreshes the expected-path host state after the action'
                Assert-Equal 71 $record.host_pid 'log records the host PID observed after startup'
                Assert-Equal $expectedPath $record.host_path 'log records the expected host path observed after startup'
            }
            finally { Remove-TestTree $tree }
        }
        'manager-retries-when-runtime-current' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree -ReleaseId 'release-a'
                Write-TestRelease -Tree $tree -ReleaseId 'release-a' -ManagerBytes 'retried-manager' | Out-Null
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 61; Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe') }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 0 $state.Stopped.Count 'current runtime manager retry does not stop host'
                Assert-Equal 0 $state.Starts 'current runtime manager retry does not start host'
                Assert-Equal 'retried-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'service_host_manager.ps1')) `
                    'valid manifest retries manager replacement after an earlier manager failure'
            }
            finally { Remove-TestTree $tree }
        }
        'manager-reconciliation-owned-once-after-update' {
            $tree = New-TestTree
            $originalUpdateManager = (Get-Command Update-InstalledManager).ScriptBlock
            try {
                Write-TestRelease -Tree $tree -ReleaseId 'release-b' -ManagerBytes 'single-owner-manager' | Out-Null
                Write-InstalledRelease -Tree $tree -ReleaseId 'release-a'
                $state = [pscustomobject]@{ ManagerCalls = 0; Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{
                        ProcessName = 'QuickBooksServiceHost'
                        Id = 81
                        Path = (Join-Path $tree.Install 'QuickBooksServiceHost.exe')
                    }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                $replacement = {
                    param($Manifest, $SharePath, $StatePath)
                    $state.ManagerCalls++
                    & $originalUpdateManager -Manifest $Manifest -SharePath $SharePath -StatePath $StatePath
                    if ($state.ManagerCalls -eq 1) {
                        Remove-Item -LiteralPath $SharePath -Recurse -Force
                    }
                }.GetNewClosure()
                Set-Item -Path Function:\Update-InstalledManager -Value $replacement

                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null

                Assert-Equal 1 $state.ManagerCalls 'manager reconciliation has one owner after runtime outcome is final'
                Assert-Equal 'new-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'completed runtime update remains installed after the share disappears'
                Assert-Equal 'single-owner-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'service_host_manager.ps1')) `
                    'single manager reconciliation completes before the share disappears'
                $record = Get-Content -Raw (Join-Path $tree.StatePath 'service-host-manager.log') | ConvertFrom-Json
                Assert-Equal 'apply-update' $record.decision 'completed update retains its logged decision'
                Assert-Equal 'updated' $record.result 'completed update retains its logged result'
            }
            finally {
                Set-Item -Path Function:\Update-InstalledManager -Value $originalUpdateManager
                Remove-TestTree $tree
            }
        }
        default {
            throw "Unknown scenario: $Name"
        }
    }
}

$allScenarios = @(
    'decision-offline-host-missing',
    'decision-current-host-running',
    'decision-current-host-missing',
    'decision-invalid-manifest-host-missing',
    'decision-update-idle',
    'decision-update-qb-open',
    'decision-update-connector-running',
    'decision-no-installed-manifest',
    'manifest-reads-valid',
    'manifest-rejects-traversal',
    'manifest-rejects-schema',
    'manifest-rejects-release-id',
    'manifest-rejects-duplicate',
    'manifest-rejects-length',
    'manifest-rejects-hash',
    'update-success',
    'update-rolls-back',
    'stage-hash-fails-before-stop',
    'share-offline-starts-installed',
    'qb-defers',
    'cli-defers',
    'mutex-skips-overlap',
    'manager-self-updates',
    'logs-rotate',
    'logs-redact',
    'wrong-path-host-replaced',
    'hidden-startup',
    'previous-cleanup-failure-preserves-install',
    'move-current-failure-preserves-install',
    'partial-stop-failure-restarts-installed',
    'manager-retries-when-runtime-current',
    'activity-race-defers-after-staging',
    'logs-refresh-host-after-action',
    'manager-reconciliation-owned-once-after-update'
)

if ($Scenario -eq 'all') {
    foreach ($scenarioName in $allScenarios) {
        Run-Scenario -Name $scenarioName
    }
}
else {
    Run-Scenario -Name $Scenario
}
