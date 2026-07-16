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
    $managerDirectory = Join-Path $Tree.StatePath 'Manager'
    New-Item -ItemType Directory -Force -Path $managerDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $managerDirectory 'service_host_manager.ps1'), 'old-manager')
    [pscustomobject]@{
        schema_version = 1
        release_id = $ReleaseId
        published_at_utc = '2026-07-14T10:00:00Z'
        files = @()
        manager = $null
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Tree.StatePath 'release.manifest.json')
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
                    -StopHost $stop -StartHost $start -TestHost $health | Out-Null
                Assert-Equal 1 $state.Stops 'host stopped once after staging verifies'
                Assert-Equal 1 $state.Starts 'new host started once'
                Assert-Equal 'new-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'new runtime installed'
                Assert-Equal 'release-b' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
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
                Assert-Equal 2 $state.Stops 'rollback stops old and failed candidate hosts before runtime movement'
                Assert-Equal 2 $state.Starts 'rollback transaction starts new then previous host'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'failed replacement restores old runtime bytes'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
                    'failed replacement restores old installed manifest'
                Assert-Equal 'old-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'Manager\service_host_manager.ps1')) `
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
                    -TestHost $actions.TestHost -TestProcessExitedAction { param($item) $true } `
                    -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 'new-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'Manager\service_host_manager.ps1')) `
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
        'qb-defers-missing-host-starts-installed' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                Write-TestRelease -Tree $tree | Out-Null
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QBW32'; Id = 22; Path = 'C:\Fake\QBW32.exe' }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 1 $state.Starts 'QuickBooks deferral starts the missing installed host exactly once'
                Assert-Equal 0 $state.Stopped.Count 'QuickBooks deferral does not stop QuickBooks or any host process'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'QuickBooks deferral does not promote the available runtime'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
                    'QuickBooks deferral preserves the installed release manifest'
            }
            finally { Remove-TestTree $tree }
        }
        'cli-defers-missing-host-starts-installed' {
            $tree = New-TestTree
            try {
                Write-InstalledRelease -Tree $tree
                Write-TestRelease -Tree $tree | Out-Null
                $state = [pscustomobject]@{ Starts = 0; Stopped = @(); MutexName = '' }
                $processes = @(
                    [pscustomobject]@{ ProcessName = 'QuickBooksConnectorCli'; Id = 23; Path = 'C:\Fake\QuickBooksConnectorCli.exe' }
                )
                $actions = New-OrchestrationActions -State $state -Processes $processes
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction $actions.GetProcesses `
                    -StopProcessAction $actions.StopProcess -StartHost $actions.StartHost `
                    -TestHost $actions.TestHost -MutexAction $actions.Mutex | Out-Null
                Assert-Equal 1 $state.Starts 'connector deferral starts the missing installed host exactly once'
                Assert-Equal 0 $state.Stopped.Count 'connector deferral does not stop the connector or any host process'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'connector deferral does not promote the available runtime'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
                    'connector deferral preserves the installed release manifest'
            }
            finally { Remove-TestTree $tree }
        }
        'mutex-skips-overlap' {
            $tree = New-TestTree
            try {
                Remove-Item -LiteralPath $tree.StatePath -Recurse -Force
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
                Assert-True (-not (Test-Path -LiteralPath $tree.StatePath)) 'lost mutex with absent StatePath creates nothing'
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
                $logDirectory = Join-Path $tree.StatePath 'Logs'
                New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
                $logPath = Join-Path $logDirectory 'service-host-manager.log'
                [IO.File]::WriteAllText($logPath, ('x' * (1MB - 10)))
                foreach ($index in 1..5) {
                    [IO.File]::WriteAllText("$logPath.$index", "old-$index")
                }
                Write-ManagerLog -StatePath $tree.StatePath -Record ([pscustomobject]@{
                    installed_release = 'a'; available_release = 'b'; decision = 'apply-update'
                    result = ('y' * 200); rollback = $false; host_pid = 1; host_path = 'C:\fake\host.exe'
                })
                Assert-True (Test-Path -LiteralPath "$logPath.1") 'oversized log rotates to .1'
                Assert-True (Test-Path -LiteralPath "$logPath.4") 'log rotation retains four backups'
                Assert-True (-not (Test-Path -LiteralPath "$logPath.5")) 'log rotation removes the fifth backup'
                Assert-Equal 5 @(Get-ChildItem -LiteralPath $logDirectory -Filter 'service-host-manager.log*').Count 'log retention is active plus .1 through .4'
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
                $line = Get-Content -Raw (Join-Path (Join-Path $tree.StatePath 'Logs') 'service-host-manager.log')
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
        'logs-use-state-logs-directory' {
            $tree = New-TestTree
            try {
                $logDirectory = Join-Path $tree.StatePath 'Logs'
                Assert-True (-not (Test-Path -LiteralPath $logDirectory)) 'test begins without the manager log directory'
                Write-ManagerLog -StatePath $tree.StatePath -Record ([pscustomobject]@{
                    installed_release = 'a'; available_release = 'b'; decision = 'keep-running'
                    result = 'running'; rollback = $false; host_pid = 1; host_path = 'C:\fake\host.exe'
                })
                $logPath = Join-Path $logDirectory 'service-host-manager.log'
                Assert-True (Test-Path -LiteralPath $logDirectory -PathType Container) `
                    'manager logging creates StatePath\Logs'
                Assert-True (Test-Path -LiteralPath $logPath -PathType Leaf) `
                    'manager logging writes the exact StatePath\Logs\service-host-manager.log path'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $tree.StatePath 'service-host-manager.log'))) `
                    'manager logging does not write the legacy StatePath root log'
            }
            finally { Remove-TestTree $tree }
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
                Assert-Equal 0 $state.Stops 'cleanup failure is detected before host stop'
                Assert-Equal 0 $state.Starts 'pre-stop cleanup failure needs no recovery start'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'cleanup failure must not delete the still-good install'
                Assert-Equal 'stale-previous' (Get-Content -Raw (Join-Path $previousPath 'QuickBooksServiceHost.exe')) `
                    'cleanup failure must not restore stale Previous over the install'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
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
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
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
                Assert-Equal 0 $state.Starts 'partial stop failure does not duplicate a surviving healthy host'
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
        'late-deferral-resnap-avoids-duplicate-host-start' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree | Out-Null
                Write-InstalledRelease -Tree $tree -ReleaseId 'release-a'
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $expectedHost = [pscustomobject]@{
                    ProcessName = 'QuickBooksServiceHost'
                    Id = 72
                    Path = $expectedPath
                }
                $quickBooks = [pscustomobject]@{ ProcessName = 'QBW32'; Id = 73; Path = 'C:\QuickBooks\QBW32.exe' }
                $state = [pscustomobject]@{ Calls = 0; Stops = 0; Starts = 0; MutexName = '' }
                $getProcesses = {
                    $state.Calls++
                    if ($state.Calls -eq 1) { return @() }
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

                Assert-Equal 0 $state.Stops 'late activity deferral does not stop the host that appeared during staging'
                Assert-Equal 0 $state.Starts 'late activity deferral does not start a duplicate expected-path host'
                Assert-True ($state.Calls -ge 4) 'deferred startup re-snapshots the expected-path host before deciding to start'
                Assert-Equal 'old-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'late activity deferral does not promote the available runtime'
                Assert-Equal 'release-a' ((Get-Content -Raw (Join-Path $tree.StatePath 'release.manifest.json') | ConvertFrom-Json).release_id) `
                    'late activity deferral preserves the installed release manifest'
                $record = Get-Content -Raw (Join-Path (Join-Path $tree.StatePath 'Logs') 'service-host-manager.log') | ConvertFrom-Json
                Assert-Equal 'defer-update' $record.decision 'late activity retains the deferred decision'
                Assert-Equal 'deferred' $record.result 'late activity retains the deferred result'
                Assert-Equal 72 $record.host_pid 'the expected-path host remains available after deferral'
                Assert-Equal $expectedPath $record.host_path 'logging observes the host that appeared during staging'
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

                $record = Get-Content -Raw (Join-Path (Join-Path $tree.StatePath 'Logs') 'service-host-manager.log') | ConvertFrom-Json
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
                Assert-Equal 'retried-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'Manager\service_host_manager.ps1')) `
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
                    -TestHost $actions.TestHost -TestProcessExitedAction { param($item) $true } `
                    -MutexAction $actions.Mutex | Out-Null

                Assert-Equal 1 $state.ManagerCalls 'manager reconciliation has one owner after runtime outcome is final'
                Assert-Equal 'new-bytes' (Get-Content -Raw (Join-Path $tree.Install 'QuickBooksServiceHost.exe')) `
                    'completed runtime update remains installed after the share disappears'
                Assert-Equal 'single-owner-manager' (Get-Content -Raw (Join-Path $tree.StatePath 'Manager\service_host_manager.ps1')) `
                    'single manager reconciliation completes before the share disappears'
                $record = Get-Content -Raw (Join-Path (Join-Path $tree.StatePath 'Logs') 'service-host-manager.log') | ConvertFrom-Json
                Assert-Equal 'apply-update' $record.decision 'completed update retains its logged decision'
                Assert-Equal 'updated' $record.result 'completed update retains its logged result'
            }
            finally {
                Set-Item -Path Function:\Update-InstalledManager -Value $originalUpdateManager
                Remove-TestTree $tree
            }
        }
        'phase3-cleanup-fails-closed' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $previousPath = Join-Path $tree.StatePath 'Previous'
                New-Item -ItemType Directory -Force -Path $previousPath | Out-Null
                [IO.File]::WriteAllText((Join-Path $previousPath 'stale.txt'), 'stale')
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $removeAction = {
                    param($path, [bool]$recurse)
                    if ($path -ieq $previousPath) { return }
                    Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction Stop
                }.GetNewClosure()
                $stop = { $state.Stops++ }.GetNewClosure()
                $start = { $state.Starts++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost $start -TestHost { $true } `
                        -RemovePathAction $removeAction
                } "Path cleanup could not be verified absent: $previousPath" `
                    'Previous cleanup must fail closed before host stop'
                Assert-Equal 0 $state.Stops 'failed Previous cleanup occurs before host stop'
                Assert-Equal 'stale' ([IO.File]::ReadAllText((Join-Path $previousPath 'stale.txt'))) `
                    'failed Previous cleanup preserves the retained tree'
            }
            finally { Remove-TestTree $tree }

            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ CleanupPath = '' }
                $removeAction = {
                    param($path, [bool]$recurse)
                    $state.CleanupPath = $path
                }.GetNewClosure()
                $caught = $null
                try {
                    Update-InstalledManager -Manifest $manifest -SharePath $tree.Share `
                        -StatePath $tree.StatePath -RemovePathAction $removeAction
                }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'manager candidate cleanup failure must throw'
                Assert-Equal "Path cleanup could not be verified absent: $($state.CleanupPath)" `
                    $caught.Exception.Message 'manager cleanup failure is precise'
                Assert-Equal $tree.StatePath (Split-Path -Parent $state.CleanupPath) `
                    'manager candidate uses a direct protected StatePath child'
                Assert-True ((Split-Path -Leaf $state.CleanupPath) -match '^[0-9a-f]{32}$') `
                    'manager candidate uses an unpredictable GUID stage root'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-guid-stage-retains-previous' {
            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ StageRoot = ''; Stops = 0; Starts = 0 }
                $moveAction = {
                    param($source, $destination)
                    if ($destination -ieq $tree.Install -and $source -ine $tree.StatePath) {
                        $state.StageRoot = Split-Path -Parent $source
                    }
                    Move-Item -LiteralPath $source -Destination $destination
                }.GetNewClosure()
                Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                    -InstallPath $tree.Install -StatePath $tree.StatePath `
                    -StopHost { $state.Stops++ }.GetNewClosure() `
                    -StartHost { $state.Starts++ }.GetNewClosure() -TestHost { $true } `
                    -MovePathAction $moveAction | Out-Null
                Assert-Equal $tree.StatePath (Split-Path -Parent $state.StageRoot) `
                    'runtime stage is a direct protected StatePath child'
                Assert-True ((Split-Path -Leaf $state.StageRoot) -match '^[0-9a-f]{32}$') `
                    'runtime stage uses an unpredictable GUID root'
                Assert-True (-not (Test-Path -LiteralPath $state.StageRoot)) `
                    'runtime stage root is verified absent after success'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $tree.StatePath 'Stage'))) `
                    'predictable fixed Stage is never used'
                Assert-Equal 'old-bytes' ([IO.File]::ReadAllText((Join-Path $tree.StatePath 'Previous\QuickBooksServiceHost.exe'))) `
                    'success retains exactly the prior runtime in Previous'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-delayed-exit-before-move' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree | Out-Null
                Write-InstalledRelease -Tree $tree
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $process = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 91; Path = $expectedPath }
                $state = [pscustomobject]@{ ExitChecks = 0; Delays = 0; Stops = 0; Starts = 0; Moved = $false }
                $testExited = {
                    param($capturedProcess)
                    $state.ExitChecks++
                    return $state.ExitChecks -ge 3
                }.GetNewClosure()
                $delay = { param($milliseconds) $state.Delays++ }.GetNewClosure()
                $moveAction = {
                    param($source, $destination)
                    if ($source -ieq $tree.Install) {
                        Assert-True ($state.ExitChecks -ge 3) 'runtime must not move while a captured expected host is alive'
                        $state.Moved = $true
                    }
                    Move-Item -LiteralPath $source -Destination $destination
                }.GetNewClosure()
                $mutex = { param($name, $action) & $action }
                Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                    -StatePath $tree.StatePath -GetProcessesAction { @($process) }.GetNewClosure() `
                    -StopProcessAction { param($item) $state.Stops++ }.GetNewClosure() `
                    -StartHost { $state.Starts++ }.GetNewClosure() -TestHost { $true } `
                    -TestProcessExitedAction $testExited -DelayAction $delay `
                    -HostExitTimeoutMilliseconds 1000 -HostExitPollMilliseconds 100 `
                    -MovePathAction $moveAction -MutexAction $mutex | Out-Null
                Assert-Equal 1 $state.Stops 'captured expected host is stopped once'
                Assert-Equal 3 $state.ExitChecks 'exit polling continues until the delayed host exits'
                Assert-Equal 2 $state.Delays 'bounded wait delays between failed exit checks'
                Assert-True $state.Moved 'runtime moves after the captured expected host exits'
                $unverifiable = New-Object psobject -Property @{ Id = 92 }
                $unverifiable | Add-Member -MemberType ScriptProperty -Name HasExited -Value { throw 'access denied' }
                Assert-True (-not (Test-CapturedHostProcessExited -Process $unverifiable)) `
                    'an exit state that cannot be observed fails closed'
                $missingExitState = [pscustomobject]@{ Id = 93 }
                Assert-True (-not (Test-CapturedHostProcessExited -Process $missingExitState)) `
                    'a captured process without HasExited fails closed'
                if (-not ('Phase3ThrowingExitProcess' -as [type])) {
                    Add-Type -TypeDefinition @"
public sealed class Phase3ThrowingExitProcess {
    public int Id { get { return 94; } }
    public bool HasExited { get { throw new System.InvalidOperationException("access denied"); } }
    public void Refresh() { }
}
"@
                }
                $throwingExitState = New-Object Phase3ThrowingExitProcess
                Assert-True (-not (Test-CapturedHostProcessExited -Process $throwingExitState)) `
                    'a captured process whose HasExited getter throws fails closed'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-default-health-is-strict-and-stable' {
            $tree = New-TestTree
            try {
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $expectedOne = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 101; Path = $expectedPath }
                $expectedTwo = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 102; Path = $expectedPath }
                $wrong = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 103; Path = 'C:\Wrong\QuickBooksServiceHost.exe' }
                Assert-True (-not (Test-InstalledHostStable -InstallPath $tree.Install `
                    -GetProcessesAction { @($expectedOne, $expectedTwo) }.GetNewClosure() `
                    -DelayAction { param($milliseconds) } -StabilityDelayMilliseconds 1)) `
                    'duplicate expected-path final hosts are unhealthy'
                Assert-True (-not (Test-InstalledHostStable -InstallPath $tree.Install `
                    -GetProcessesAction { @($expectedOne, $wrong) }.GetNewClosure() `
                    -DelayAction { param($milliseconds) } -StabilityDelayMilliseconds 1)) `
                    'an expected host plus a wrong-path duplicate is unhealthy'

                $state = [pscustomobject]@{ Snapshots = 0; Delays = 0 }
                $snapshots = {
                    $state.Snapshots++
                    if ($state.Snapshots -eq 1) { return @($expectedOne) }
                    return @($expectedOne, $wrong)
                }.GetNewClosure()
                $delay = { param($milliseconds) $state.Delays++ }.GetNewClosure()
                Assert-True (-not (Test-InstalledHostStable -InstallPath $tree.Install `
                    -GetProcessesAction $snapshots -DelayAction $delay -StabilityDelayMilliseconds 1)) `
                    'host health must remain valid across the stability window'
                Assert-Equal 2 $state.Snapshots 'stable health takes two injected snapshots'
                Assert-Equal 1 $state.Delays 'stable health uses one injected delay window'
                Assert-True (Test-InstalledHostStable -InstallPath $tree.Install `
                    -GetProcessesAction { @($expectedOne) }.GetNewClosure() `
                    -DelayAction { param($milliseconds) } -StabilityDelayMilliseconds 1) `
                    'exactly one expected-path host across both snapshots is healthy'

                $replacement = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 104; Path = $expectedPath }
                $replacementState = [pscustomobject]@{ Snapshots = 0 }
                $replacementSnapshots = {
                    $replacementState.Snapshots++
                    if ($replacementState.Snapshots -eq 1) { return @($expectedOne) }
                    return @($replacement)
                }.GetNewClosure()
                Assert-True (-not (Test-InstalledHostStable -InstallPath $tree.Install `
                    -GetProcessesAction $replacementSnapshots `
                    -DelayAction { param($milliseconds) } -StabilityDelayMilliseconds 1)) `
                    'a same-path replacement process is not the same stable host'

            }
            finally { Remove-TestTree $tree }
        }
        'phase3-manager-failure-preserves-runtime-and-retries' {
            $tree = New-TestTree
            $originalUpdateManager = (Get-Command Update-InstalledManager).ScriptBlock
            try {
                Write-TestRelease -Tree $tree -ManagerBytes 'retry-manager' | Out-Null
                Write-InstalledRelease -Tree $tree
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $process = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 111; Path = $expectedPath }
                $state = [pscustomobject]@{ ManagerCalls = 0; Stops = 0; Starts = 0 }
                $replacement = {
                    param($Manifest, $SharePath, $StatePath)
                    $state.ManagerCalls++
                    if ($state.ManagerCalls -eq 1) { throw 'simulated manager copy failure' }
                    & $originalUpdateManager -Manifest $Manifest -SharePath $SharePath -StatePath $StatePath
                }.GetNewClosure()
                Set-Item -Path Function:\Update-InstalledManager -Value $replacement
                $mutex = { param($name, $action) & $action }
                $common = @{
                    SharePath = $tree.Share; InstallPath = $tree.Install; StatePath = $tree.StatePath
                    GetProcessesAction = { @($process) }.GetNewClosure()
                    StopProcessAction = { param($item) $state.Stops++ }.GetNewClosure()
                    TestProcessExitedAction = { param($item) $true }
                    StartHost = { $state.Starts++ }.GetNewClosure(); TestHost = { $true }
                    MutexAction = $mutex
                }
                Invoke-ServiceHostManager @common | Out-Null
                Assert-Equal 'new-bytes' ([IO.File]::ReadAllText((Join-Path $tree.Install 'QuickBooksServiceHost.exe'))) `
                    'successful runtime remains installed when manager reconciliation fails'
                Assert-Equal 'old-manager' ([IO.File]::ReadAllText((Join-Path $tree.StatePath 'Manager\service_host_manager.ps1'))) `
                    'failed manager reconciliation leaves the prior manager for retry'
                $logPath = Join-Path $tree.StatePath 'Logs\service-host-manager.log'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'updated' $record.result 'runtime success remains the structured result'
                Assert-Equal 'failed' $record.manager_update 'manager failure is structured separately'
                Assert-Equal $true $record.manager_retry 'manager failure explicitly remains retryable'

                Invoke-ServiceHostManager @common | Out-Null
                Assert-Equal 2 $state.ManagerCalls 'later manager invocation retries reconciliation'
                Assert-Equal 'retry-manager' ([IO.File]::ReadAllText((Join-Path $tree.StatePath 'Manager\service_host_manager.ps1'))) `
                    'later reconciliation installs the verified manager'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'succeeded' $record.manager_update 'successful retry is structured'
                Assert-Equal $false $record.manager_retry 'successful retry clears retry posture'
            }
            finally {
                Set-Item -Path Function:\Update-InstalledManager -Value $originalUpdateManager
                Remove-TestTree $tree
            }
        }
        'phase3-rollback-outcome-and-log' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree | Out-Null
                Write-InstalledRelease -Tree $tree
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $process = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 121; Path = $expectedPath }
                $state = [pscustomobject]@{ Stops = 0; Starts = 0 }
                $caught = $null
                try {
                    Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                        -StatePath $tree.StatePath -GetProcessesAction { @($process) }.GetNewClosure() `
                        -StopProcessAction { param($item) $state.Stops++ }.GetNewClosure() `
                        -StartHost { $state.Starts++ }.GetNewClosure() -TestHost { $false } `
                        -MutexAction { param($name, $action) & $action } | Out-Null
                }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'failed candidate and failed restoration report failure'
                $logPath = Join-Path $tree.StatePath 'Logs\service-host-manager.log'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'failed' $record.result 'failed runtime outcome is structured'
                Assert-Equal $true $record.rollback 'rollback is true whenever restoration was attempted'
            }
            finally { Remove-TestTree $tree }

            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $state = [pscustomobject]@{ Starts = 0; Healthy = $false }
                $outcome = [pscustomobject]@{ RollbackAttempted = $false; RollbackSucceeded = $false }
                $start = {
                    $state.Starts++
                    if ($state.Starts -eq 2) { $state.Healthy = $true }
                }.GetNewClosure()
                $health = { $state.Healthy }.GetNewClosure()
                Assert-Throws {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath -StopHost { } `
                        -StartHost $start -TestHost $health -Outcome $outcome
                } 'restored release still reports the failed candidate'
                Assert-Equal $true $outcome.RollbackAttempted 'transaction outcome records attempted restoration'
                Assert-Equal $true $outcome.RollbackSucceeded 'transaction outcome records successful restoration'
            }
            finally { Remove-TestTree $tree }

            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $previousPath = Join-Path $tree.StatePath 'Previous'
                $state = [pscustomobject]@{ Starts = 0; Healthy = $false }
                $outcome = [pscustomobject]@{ RollbackAttempted = $false; RollbackSucceeded = $false }
                $start = {
                    $state.Starts++
                    if ($state.Starts -eq 2) { $state.Healthy = $true }
                }.GetNewClosure()
                $health = { $state.Healthy }.GetNewClosure()
                $moveAction = {
                    param($source, $destination)
                    Move-Item -LiteralPath $source -Destination $destination
                    if ($source -ieq $previousPath -and $destination -ieq $tree.Install) {
                        throw 'simulated restore move-then-throw'
                    }
                }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath -StopHost { } `
                        -StartHost $start -TestHost $health -MovePathAction $moveAction -Outcome $outcome
                } 'Release failed; previous host restored.' `
                    'rollback reconciles a successful restore move that throws afterward'
                Assert-Equal $true $outcome.RollbackAttempted 'move-then-throw rollback remains attempted'
                Assert-Equal $true $outcome.RollbackSucceeded 'move-then-throw rollback is recorded as successful'
                Assert-Equal 'old-bytes' ([IO.File]::ReadAllText((Join-Path $tree.Install 'QuickBooksServiceHost.exe'))) `
                    'move-then-throw rollback restores the old runtime'
            }
            finally { Remove-TestTree $tree }

            $tree = New-TestTree
            try {
                $manifest = Write-TestRelease -Tree $tree
                Write-InstalledRelease -Tree $tree
                $target = Join-Path $tree.Root 'runtime-target'
                Move-Item -LiteralPath $tree.Install -Destination $target
                New-Item -ItemType Junction -Path $tree.Install -Target $target -ErrorAction Stop | Out-Null
                $state = [pscustomobject]@{ Stops = 0 }
                $stop = { $state.Stops++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Invoke-ReleaseTransaction -Manifest $manifest -SharePath $tree.Share `
                        -InstallPath $tree.Install -StatePath $tree.StatePath `
                        -StopHost $stop -StartHost { } -TestHost { $true }
                } "Reparse points are not allowed in manager transaction state: $($tree.Install)" `
                    'runtime reparse substitution fails before host stop or runtime movement'
                Assert-Equal 0 $state.Stops 'runtime reparse substitution is rejected before host stop'
                Assert-Equal 'old-bytes' ([IO.File]::ReadAllText((Join-Path $target 'QuickBooksServiceHost.exe'))) `
                    'runtime reparse substitution leaves the target untouched'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-reparse-cleanup-is-rejected' {
            $tree = New-TestTree
            try {
                $target = Join-Path $tree.Root 'reparse-target'
                $link = Join-Path $tree.StatePath 'Previous'
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                [IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'), 'safe')
                New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop | Out-Null
                $state = [pscustomobject]@{ Removes = 0 }
                $removeAction = { param($path, [bool]$recurse) $state.Removes++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Remove-ManagerPathVerified -Path $link -Recurse $true -RemovePathAction $removeAction
                } "Reparse points are not allowed in manager transaction state: $link" `
                    'cleanup rejects a substituted reparse point before removal'
                Assert-Equal 0 $state.Removes 'reparse cleanup invokes no removal action'
                Assert-Equal 'safe' ([IO.File]::ReadAllText((Join-Path $target 'sentinel.txt'))) `
                    'reparse cleanup leaves the target untouched'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-manager-cleanup-orphan-recovers' {
            $tree = New-TestTree
            $originalUpdateManager = (Get-Command Update-InstalledManager).ScriptBlock
            try {
                Write-TestRelease -Tree $tree -ManagerBytes 'orphan-recovery-manager' | Out-Null
                Write-InstalledRelease -Tree $tree
                $expectedPath = Join-Path $tree.Install 'QuickBooksServiceHost.exe'
                $process = [pscustomobject]@{ ProcessName = 'QuickBooksServiceHost'; Id = 141; Path = $expectedPath }
                $state = [pscustomobject]@{ ManagerCalls = 0; OrphanPath = '' }
                $replacement = {
                    param($Manifest, $SharePath, $StatePath)
                    $state.ManagerCalls++
                    if ($state.ManagerCalls -eq 1) {
                        $leaveOrphan = {
                            param($path, [bool]$recurse)
                            $state.OrphanPath = $path
                        }.GetNewClosure()
                        & $originalUpdateManager -Manifest $Manifest -SharePath $SharePath `
                            -StatePath $StatePath -RemovePathAction $leaveOrphan
                        return
                    }
                    & $originalUpdateManager -Manifest $Manifest -SharePath $SharePath -StatePath $StatePath
                }.GetNewClosure()
                Set-Item -Path Function:\Update-InstalledManager -Value $replacement
                $common = @{
                    SharePath = $tree.Share; InstallPath = $tree.Install; StatePath = $tree.StatePath
                    GetProcessesAction = { @($process) }.GetNewClosure()
                    StopProcessAction = { param($item) }
                    TestProcessExitedAction = { param($item) $true }
                    StartHost = { }; TestHost = { $true }
                    MutexAction = { param($name, $action) & $action }
                }

                Invoke-ServiceHostManager @common | Out-Null
                $logPath = Join-Path $tree.StatePath 'Logs\service-host-manager.log'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'failed' $record.manager_update 'cleanup-only manager failure is structured'
                Assert-Equal $true $record.manager_recovery_required 'cleanup-only manager failure exposes recovery posture'
                $orphanPath = [string]$record.manager_recovery_path
                Assert-True ($orphanPath -ne '') 'cleanup-only manager failure preserves the orphan path'
                Assert-True (Test-Path -LiteralPath $orphanPath -PathType Container) 'failed cleanup leaves one known orphan'
                $recoveryStatePath = Join-Path $tree.StatePath 'Manager\manager-update-recovery.json'
                Assert-True (Test-Path -LiteralPath $recoveryStatePath -PathType Leaf) 'cleanup failure persists recovery state'
                Assert-Equal 1 @((Get-ChildItem -LiteralPath $tree.StatePath -Directory) | Where-Object { $_.Name -match '^[0-9a-f]{32}$' }).Count `
                    'first cleanup failure leaves exactly one GUID transaction directory'

                Invoke-ServiceHostManager @common | Out-Null
                Assert-Equal 2 $state.ManagerCalls 'later orchestration retries manager reconciliation'
                Assert-True (-not (Test-Path -LiteralPath $orphanPath)) 'retry cleans the recorded orphan first'
                Assert-True (-not (Test-Path -LiteralPath $recoveryStatePath)) 'retry clears recovery state after cleanup'
                Assert-Equal 0 @((Get-ChildItem -LiteralPath $tree.StatePath -Directory) | Where-Object { $_.Name -match '^[0-9a-f]{32}$' }).Count `
                    'retry does not accumulate more GUID transaction directories'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'succeeded' $record.manager_update 'retry succeeds after orphan cleanup'
                Assert-Equal $false $record.manager_recovery_required 'successful retry clears recovery posture'
            }
            finally {
                Set-Item -Path Function:\Update-InstalledManager -Value $originalUpdateManager
                Remove-TestTree $tree
            }
        }
        'phase3-nested-reparse-cleanup-is-rejected' {
            $tree = New-TestTree
            try {
                $cleanupRoot = Join-Path $tree.StatePath 'cleanup-root'
                $nested = Join-Path $cleanupRoot 'nested'
                $target = Join-Path $tree.Root 'nested-reparse-target'
                $link = Join-Path $nested 'link'
                New-Item -ItemType Directory -Force -Path $nested, $target | Out-Null
                [IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'), 'safe')
                New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop | Out-Null
                $state = [pscustomobject]@{ Removes = 0 }
                $removeAction = { param($path, [bool]$recurse) $state.Removes++ }.GetNewClosure()
                Assert-ThrowsMessage {
                    Remove-ManagerPathVerified -Path $cleanupRoot -Recurse $true -RemovePathAction $removeAction
                } "Reparse points are not allowed in manager transaction state: $link" `
                    'recursive cleanup rejects a nested reparse descendant before removal'
                Assert-Equal 0 $state.Removes 'nested reparse cleanup invokes no removal action'
                Assert-Equal 'safe' ([IO.File]::ReadAllText((Join-Path $target 'sentinel.txt'))) `
                    'nested reparse cleanup leaves the external target untouched'
            }
            finally { Remove-TestTree $tree }
        }
        'phase3-no-prior-install-rollback-is-accurate' {
            $tree = New-TestTree
            try {
                Write-TestRelease -Tree $tree | Out-Null
                Remove-Item -LiteralPath $tree.Install -Recurse -Force
                $state = [pscustomobject]@{ Starts = 0 }
                $caught = $null
                try {
                    Invoke-ServiceHostManager -SharePath $tree.Share -InstallPath $tree.Install `
                        -StatePath $tree.StatePath -GetProcessesAction { @() } `
                        -StopProcessAction { param($item) } `
                        -StartHost { $state.Starts++ }.GetNewClosure() -TestHost { $false } `
                        -MutexAction { param($name, $action) & $action } | Out-Null
                }
                catch { $caught = $_ }
                Assert-True ($null -ne $caught) 'failed first install reports the candidate failure'
                Assert-Equal 'Updated host failed its health check.' $caught.Exception.Message `
                    'no-prior-install failure does not claim a previous host was restored'
                Assert-Equal $false ([bool]$caught.Exception.Data['RollbackSucceeded']) `
                    'no-prior-install failure records no successful restoration'
                $logPath = Join-Path $tree.StatePath 'Logs\service-host-manager.log'
                $record = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })[-1]
                Assert-Equal 'failed' $record.result 'failed first install is structured'
                Assert-Equal $true $record.rollback 'candidate cleanup is recorded as rollback attempted'
                Assert-Equal $false $record.rollback_succeeded 'structured log does not claim a previous host was restored'
                Assert-True (-not (Test-Path -LiteralPath $tree.Install)) 'failed candidate is removed when no prior install exists'
                Assert-Equal 1 $state.Starts 'only the failed candidate start is attempted without a previous install'
            }
            finally { Remove-TestTree $tree }
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
    'qb-defers-missing-host-starts-installed',
    'cli-defers-missing-host-starts-installed',
    'mutex-skips-overlap',
    'manager-self-updates',
    'logs-rotate',
    'logs-redact',
    'logs-use-state-logs-directory',
    'wrong-path-host-replaced',
    'hidden-startup',
    'previous-cleanup-failure-preserves-install',
    'move-current-failure-preserves-install',
    'partial-stop-failure-restarts-installed',
    'manager-retries-when-runtime-current',
    'activity-race-defers-after-staging',
    'late-deferral-resnap-avoids-duplicate-host-start',
    'logs-refresh-host-after-action',
    'manager-reconciliation-owned-once-after-update',
    'phase3-cleanup-fails-closed',
    'phase3-guid-stage-retains-previous',
    'phase3-delayed-exit-before-move',
    'phase3-default-health-is-strict-and-stable',
    'phase3-manager-failure-preserves-runtime-and-retries',
    'phase3-rollback-outcome-and-log',
    'phase3-reparse-cleanup-is-rejected',
    'phase3-manager-cleanup-orphan-recovers',
    'phase3-nested-reparse-cleanup-is-rejected',
    'phase3-no-prior-install-rollback-is-accurate'
)

if ($Scenario -eq 'all') {
    foreach ($scenarioName in $allScenarios) {
        Run-Scenario -Name $scenarioName
    }
}
else {
    Run-Scenario -Name $Scenario
}
