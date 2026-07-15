[CmdletBinding()]
param(
    [string]$SharePath = '\\PC-VS-APPFS01\CNC Process\COLTON TEST\QBUtility Beta Install\service_host_installation',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'QuickBooksServiceHost'),
    [string]$StatePath = (Join-Path $env:ProgramData 'QuickBooksServiceHost'),
    [switch]$AsLibrary
)
$ErrorActionPreference = 'Stop'

function Read-ReleaseManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifest = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    Test-ReleaseManifest -Manifest $manifest | Out-Null
    return $manifest
}

function Test-ReleaseManifest {
    param([Parameter(Mandatory = $true)]$Manifest)

    if ([int]$Manifest.schema_version -ne 1) {
        throw 'Unsupported release manifest schema.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$Manifest.release_id)) {
        throw 'Release ID is required.'
    }

    $entries = @($Manifest.files) + @($Manifest.manager)
    $seen = @{}
    foreach ($entry in $entries) {
        $path = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($path) -or
            [IO.Path]::IsPathRooted($path) -or
            $path.Split('\') -contains '..' -or
            $path.Split('/') -contains '..') {
            throw "Unsafe manifest path: $path"
        }

        $pathKey = $path.ToLowerInvariant()
        if ($seen.ContainsKey($pathKey)) {
            throw "Duplicate manifest path: $path"
        }
        $seen[$pathKey] = $true

        if ([long]$entry.length -lt 0) {
            throw "Invalid manifest length: $path"
        }
        if ([string]$entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Invalid manifest hash: $path"
        }
    }

    return $true
}

function Get-ManagerDecision {
    param(
        [bool]$ShareAvailable,
        [bool]$ManifestValid,
        [AllowEmptyString()][string]$InstalledReleaseId,
        [AllowEmptyString()][string]$AvailableReleaseId,
        [bool]$QuickBooksRunning,
        [bool]$ConnectorRunning,
        [bool]$HostRunning
    )

    if (-not $ShareAvailable -or
        -not $ManifestValid -or
        $InstalledReleaseId -eq $AvailableReleaseId) {
        if ($HostRunning) { return 'keep-running' }
        return 'start-installed'
    }
    if ($QuickBooksRunning -or $ConnectorRunning) {
        return 'defer-update'
    }
    return 'apply-update'
}

function Assert-ManagerStatePathTrusted {
    param([Parameter(Mandatory = $true)][string]$StatePath)

    $item = Get-Item -LiteralPath $StatePath -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) { throw "Service host state path is not a directory: $StatePath" }
    if (([long]$item.Attributes -band [long][IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed for the service host state path: $StatePath"
    }
}

function Assert-ManagerPathNotReparse {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (([long]$item.Attributes -band [long][IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed in manager transaction state: $Path"
    }
}

function Remove-ManagerPathVerified {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [bool]$Recurse = $true,
        [scriptblock]$RemovePathAction = {
            param($removePath, [bool]$removeRecurse)
            Remove-Item -LiteralPath $removePath -Recurse:$removeRecurse -Force -ErrorAction Stop
        }
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-ManagerPathNotReparse -Path $Path
    & $RemovePathAction $Path $Recurse | Out-Null
    if (Test-Path -LiteralPath $Path) {
        throw "Path cleanup could not be verified absent: $Path"
    }
}

function New-ManagerStageRoot {
    param([Parameter(Mandatory = $true)][string]$StatePath)

    Assert-ManagerStatePathTrusted -StatePath $StatePath
    $stageRoot = Join-Path $StatePath ([guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $stageRoot) { throw "Manager transaction stage already exists: $stageRoot" }
    New-Item -ItemType Directory -Path $stageRoot -ErrorAction Stop | Out-Null
    Assert-ManagerPathNotReparse -Path $stageRoot
    return $stageRoot
}

function Copy-VerifiedReleaseToStage {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$SharePath,
        [Parameter(Mandatory = $true)][string]$StagePath
    )

    if (Test-Path -LiteralPath $StagePath) { throw "Release stage already exists: $StagePath" }
    New-Item -ItemType Directory -Path $StagePath -ErrorAction Stop | Out-Null
    Assert-ManagerPathNotReparse -Path $StagePath
    foreach ($entry in @($Manifest.files)) {
        $source = Join-Path $SharePath ([string]$entry.path)
        $target = Join-Path $StagePath ([string]$entry.path)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
        $targetItem = Get-Item -LiteralPath $target -ErrorAction Stop
        if ([long]$targetItem.Length -ne [long]$entry.length) {
            throw "Length mismatch: $($entry.path)"
        }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash -ne [string]$entry.sha256) {
            throw "Hash mismatch: $($entry.path)"
        }
    }
}

function Start-InstalledHost {
    param(
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [scriptblock]$StartProcessAction = {
            param($filePath, $workingDirectory, $windowStyle)
            Start-Process -FilePath $filePath -WorkingDirectory $workingDirectory `
                -WindowStyle $windowStyle -PassThru
        }
    )

    $exe = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
    return (& $StartProcessAction $exe $InstallPath 'Hidden')
}

function Update-InstalledManager {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$SharePath,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [scriptblock]$RemovePathAction = {
            param($path, [bool]$recurse)
            Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction Stop
        },
        [scriptblock]$CopyFileAction = {
            param($source, $destination)
            Copy-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop
        },
        [scriptblock]$MoveFileAction = {
            param($source, $destination)
            Move-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop
        }
    )

    if ($null -eq $Manifest.manager) { return }
    Assert-ManagerStatePathTrusted -StatePath $StatePath
    $source = Join-Path $SharePath ([string]$Manifest.manager.path)
    $managerDirectory = Join-Path $StatePath 'Manager'
    if (-not (Test-Path -LiteralPath $managerDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $managerDirectory -ErrorAction Stop | Out-Null
    }
    Assert-ManagerPathNotReparse -Path $managerDirectory

    $stageRoot = New-ManagerStageRoot -StatePath $StatePath
    $stage = Join-Path $stageRoot 'service_host_manager.ps1'
    $target = Join-Path $managerDirectory 'service_host_manager.ps1'
    $primaryFailure = $null
    try {
        & $CopyFileAction $source $stage | Out-Null
        if ([long](Get-Item -LiteralPath $stage -ErrorAction Stop).Length -ne [long]$Manifest.manager.length) {
            throw "Length mismatch: $($Manifest.manager.path)"
        }
        $stageHash = (Get-FileHash -LiteralPath $stage -Algorithm SHA256 -ErrorAction Stop).Hash
        if ($stageHash -ne [string]$Manifest.manager.sha256) {
            throw "Hash mismatch: $($Manifest.manager.path)"
        }
        $targetHash = ''
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash
        }
        if ($targetHash -ne $stageHash) {
            & $MoveFileAction $stage $target | Out-Null
            if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
                (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash -ne $stageHash) {
                throw "Installed manager verification failed: $($Manifest.manager.path)"
            }
        }
    }
    catch { $primaryFailure = $_ }

    try {
        Remove-ManagerPathVerified -Path $stageRoot -Recurse $true -RemovePathAction $RemovePathAction
    }
    catch {
        if ($null -eq $primaryFailure) { throw }
        $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Message
    }
    if ($null -ne $primaryFailure) { throw $primaryFailure }
}

function Invoke-ReleaseTransaction {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$SharePath,
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)][scriptblock]$StopHost,
        [Parameter(Mandatory = $true)][scriptblock]$StartHost,
        [Parameter(Mandatory = $true)][scriptblock]$TestHost,
        [scriptblock]$TestCanStop = { $true },
        [scriptblock]$RemovePathAction = {
            param($path, [bool]$recurse)
            Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction Stop
        },
        [scriptblock]$MovePathAction = {
            param($source, $destination)
            Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
        },
        $Outcome
    )

    Assert-ManagerStatePathTrusted -StatePath $StatePath
    if ($null -eq $Outcome) {
        $Outcome = [pscustomobject]@{ Result = ''; RollbackAttempted = $false; RollbackSucceeded = $false }
    }
    elseif ($null -eq $Outcome.PSObject.Properties['Result']) {
        $Outcome | Add-Member -NotePropertyName Result -NotePropertyValue ''
    }
    $Outcome.Result = ''
    $Outcome.RollbackAttempted = $false
    $Outcome.RollbackSucceeded = $false

    $stageRoot = New-ManagerStageRoot -StatePath $StatePath
    $stagePayloadPath = Join-Path $stageRoot 'Payload'
    $previousPath = Join-Path $StatePath 'Previous'
    $manifestPath = Join-Path $StatePath 'release.manifest.json'
    $previousManifest = $null
    $hadManifest = Test-Path -LiteralPath $manifestPath -PathType Leaf
    if ($hadManifest) { $previousManifest = Get-Content -Raw -LiteralPath $manifestPath -ErrorAction Stop }

    $stopAttempted = $false
    $stopCompleted = $false
    $currentMoved = $false
    $stagePromoted = $false
    $candidateStartAttempted = $false
    try {
        Copy-VerifiedReleaseToStage -Manifest $Manifest -SharePath $SharePath -StagePath $stagePayloadPath

        if (-not (& $TestCanStop)) {
            Remove-ManagerPathVerified -Path $stageRoot -Recurse $true -RemovePathAction $RemovePathAction
            $Outcome.Result = 'deferred'
            return $Outcome
        }

        if (Test-Path -LiteralPath $InstallPath) {
            Assert-ManagerPathNotReparse -Path $InstallPath
        }
        Remove-ManagerPathVerified -Path $previousPath -Recurse $true -RemovePathAction $RemovePathAction
        $stopAttempted = $true
        & $StopHost
        $stopCompleted = $true

        if (Test-Path -LiteralPath $InstallPath -PathType Container) {
            try { & $MovePathAction $InstallPath $previousPath | Out-Null }
            finally {
                $currentMoved = (-not (Test-Path -LiteralPath $InstallPath)) -and
                    (Test-Path -LiteralPath $previousPath -PathType Container)
            }
            if (-not $currentMoved) { throw 'Installed runtime move could not be reconciled.' }
        }

        try { & $MovePathAction $stagePayloadPath $InstallPath | Out-Null }
        finally {
            $stagePromoted = (Test-Path -LiteralPath $InstallPath -PathType Container) -and
                (-not (Test-Path -LiteralPath $stagePayloadPath))
        }
        if (-not $stagePromoted) { throw 'Staged runtime promotion could not be reconciled.' }

        $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -ErrorAction Stop
        Remove-ManagerPathVerified -Path $stageRoot -Recurse $true -RemovePathAction $RemovePathAction
        $candidateStartAttempted = $true
        & $StartHost | Out-Null
        if (-not (& $TestHost)) { throw 'Updated host failed its health check.' }

        $Outcome.Result = 'updated'
        return $Outcome
    }
    catch {
        $primaryFailure = $_
        $rollbackFailure = $null
        if ($stopAttempted) {
            $Outcome.RollbackAttempted = $true
            try {
                if ($stagePromoted -and $candidateStartAttempted) {
                    & $StopHost
                }
                if ($stagePromoted) {
                    Remove-ManagerPathVerified -Path $InstallPath -Recurse $true -RemovePathAction $RemovePathAction
                }
                if ($currentMoved -and (Test-Path -LiteralPath $previousPath -PathType Container)) {
                    $restoreMoveFailure = $null
                    try { & $MovePathAction $previousPath $InstallPath | Out-Null }
                    catch { $restoreMoveFailure = $_ }
                    $restoreReconciled = (-not (Test-Path -LiteralPath $previousPath)) -and
                        (Test-Path -LiteralPath $InstallPath -PathType Container)
                    if (-not $restoreReconciled) {
                        if ($null -ne $restoreMoveFailure) { throw $restoreMoveFailure }
                        throw "Previous restoration could not be verified consumed: $previousPath"
                    }
                    if ($null -ne $restoreMoveFailure) {
                        $primaryFailure.Exception.Data['RestoreMoveDiagnostic'] = $restoreMoveFailure.Exception.Message
                    }
                }
                if ($hadManifest) {
                    Set-Content -LiteralPath $manifestPath -Value $previousManifest -NoNewline -ErrorAction Stop
                }
                else {
                    Remove-ManagerPathVerified -Path $manifestPath -Recurse $false -RemovePathAction $RemovePathAction
                }
                if (Test-Path -LiteralPath $InstallPath -PathType Container) {
                    $healthyHostSurvived = (-not $stopCompleted) -and (& $TestHost)
                    if (-not $healthyHostSurvived) {
                        & $StartHost | Out-Null
                        if (-not (& $TestHost)) { throw 'Previous host failed its health check after restoration.' }
                    }
                }
                $Outcome.RollbackSucceeded = $true
            }
            catch { $rollbackFailure = $_ }
        }

        try {
            Remove-ManagerPathVerified -Path $stageRoot -Recurse $true -RemovePathAction $RemovePathAction
        }
        catch { $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Message }

        $primaryFailure.Exception.Data['RollbackAttempted'] = [bool]$Outcome.RollbackAttempted
        $primaryFailure.Exception.Data['RollbackSucceeded'] = [bool]$Outcome.RollbackSucceeded
        if ($null -ne $rollbackFailure) {
            $failure = [InvalidOperationException]::new('Release failed and previous host failed to restart.', $primaryFailure.Exception)
            $failure.Data['RollbackAttempted'] = $true
            $failure.Data['RollbackSucceeded'] = $false
            $failure.Data['RollbackFailure'] = $rollbackFailure.Exception.Message
            if ($null -ne $primaryFailure.Exception.Data['StageCleanupFailure']) {
                $failure.Data['StageCleanupFailure'] = $primaryFailure.Exception.Data['StageCleanupFailure']
            }
            throw $failure
        }
        if ($Outcome.RollbackSucceeded -and ($currentMoved -or $stagePromoted)) {
            $failure = [InvalidOperationException]::new('Release failed; previous host restored.', $primaryFailure.Exception)
            $failure.Data['RollbackAttempted'] = $true
            $failure.Data['RollbackSucceeded'] = $true
            if ($null -ne $primaryFailure.Exception.Data['StageCleanupFailure']) {
                $failure.Data['StageCleanupFailure'] = $primaryFailure.Exception.Data['StageCleanupFailure']
            }
            throw $failure
        }
        throw $primaryFailure
    }
}

function Rotate-ManagerLogs {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [long]$IncomingLength = 0
    )

    $logDirectory = Join-Path $StatePath 'Logs'
    $logPath = Join-Path $logDirectory 'service-host-manager.log'
    foreach ($candidate in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'service-host-manager.log.*' -File -ErrorAction SilentlyContinue)) {
        if ($candidate.Name -match '^service-host-manager\.log\.(\d+)$' -and [int]$Matches[1] -ge 5) {
            Remove-Item -LiteralPath $candidate.FullName -Force -ErrorAction Stop
        }
    }
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) { return }
    if (([long](Get-Item -LiteralPath $logPath -ErrorAction Stop).Length + $IncomingLength) -le 1MB) { return }

    Remove-ManagerPathVerified -Path "$logPath.4" -Recurse $false
    foreach ($index in 3..1) {
        $source = "$logPath.$index"
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Move-Item -LiteralPath $source -Destination "$logPath.$($index + 1)" -Force -ErrorAction Stop
        }
    }
    Move-Item -LiteralPath $logPath -Destination "$logPath.1" -Force -ErrorAction Stop
}

function Write-ManagerLog {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)]$Record
    )

    Assert-ManagerStatePathTrusted -StatePath $StatePath
    $logDirectory = Join-Path $StatePath 'Logs'
    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $logDirectory -ErrorAction Stop | Out-Null
    }
    Assert-ManagerPathNotReparse -Path $logDirectory
    $safeRecord = [ordered]@{
        timestamp_utc = [datetime]::UtcNow.ToString('o')
        installed_release = [string]$Record.installed_release
        available_release = [string]$Record.available_release
        decision = [string]$Record.decision
        result = [string]$Record.result
        rollback = [bool]$Record.rollback
        manager_update = [string]$Record.manager_update
        manager_retry = [bool]$Record.manager_retry
        host_pid = $Record.host_pid
        host_path = [string]$Record.host_path
    }
    $line = $safeRecord | ConvertTo-Json -Compress
    $incomingLength = [Text.Encoding]::UTF8.GetByteCount($line + [Environment]::NewLine)
    Rotate-ManagerLogs -StatePath $StatePath -IncomingLength $incomingLength
    $utf8NoBom = New-Object Text.UTF8Encoding -ArgumentList $false
    [IO.File]::AppendAllText((Join-Path $logDirectory 'service-host-manager.log'), `
        $line + [Environment]::NewLine, $utf8NoBom)
}

function Get-ServiceHostProcessPath {
    param($Process)
    try { return [string]$Process.Path }
    catch { return '' }
}

function Test-InstalledHostStable {
    param(
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [Parameter(Mandatory = $true)][scriptblock]$GetProcessesAction,
        [Parameter(Mandatory = $true)][scriptblock]$DelayAction,
        [int]$StabilityDelayMilliseconds = 250
    )

    $expectedPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
    foreach ($snapshotIndex in 0..1) {
        $hosts = @(& $GetProcessesAction | Where-Object { $_.ProcessName -ieq 'QuickBooksServiceHost' })
        $expectedHosts = @($hosts | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $expectedPath })
        if ($hosts.Count -ne 1 -or $expectedHosts.Count -ne 1) { return $false }
        if ($snapshotIndex -eq 0) { & $DelayAction $StabilityDelayMilliseconds | Out-Null }
    }
    return $true
}

function Test-CapturedHostProcessExited {
    param([Parameter(Mandatory = $true)]$Process)

    if ($null -eq $Process.PSObject.Properties['HasExited']) { return $true }
    try {
        if ($null -ne $Process.PSObject.Methods['Refresh']) { $Process.Refresh() }
        return [bool]$Process.HasExited
    }
    catch { return $true }
}

function Wait-CapturedHostProcessesExited {
    param(
        [object[]]$Processes,
        [Parameter(Mandatory = $true)][scriptblock]$TestProcessExitedAction,
        [Parameter(Mandatory = $true)][scriptblock]$DelayAction,
        [int]$TimeoutMilliseconds = 5000,
        [int]$PollMilliseconds = 100
    )

    if ($TimeoutMilliseconds -lt 0) { throw 'Host exit timeout must not be negative.' }
    if ($PollMilliseconds -le 0) { throw 'Host exit poll interval must be positive.' }
    $captured = @($Processes)
    if ($captured.Count -eq 0) { return }
    $maxChecks = [Math]::Max(1, [int][Math]::Ceiling($TimeoutMilliseconds / [double]$PollMilliseconds) + 1)
    foreach ($check in 1..$maxChecks) {
        $allExited = $true
        foreach ($process in $captured) {
            if (-not (& $TestProcessExitedAction $process)) { $allExited = $false }
        }
        if ($allExited) { return }
        if ($check -eq $maxChecks) {
            $ids = (@($captured | ForEach-Object { [string]$_.Id }) -join ',')
            throw "Timed out waiting for expected-path service host processes to exit: $ids"
        }
        & $DelayAction $PollMilliseconds | Out-Null
    }
}

function Invoke-WithManagerMutex {
    param([string]$Name, [scriptblock]$Action)

    $mutex = New-Object Threading.Mutex -ArgumentList $false, $Name
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { return 0 }
        return (& $Action)
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Invoke-ServiceHostManager {
    param(
        [Parameter(Mandatory = $true)][string]$SharePath,
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [scriptblock]$GetProcessesAction = { @(Get-Process -ErrorAction SilentlyContinue) },
        [scriptblock]$StopProcessAction = { param($process) Stop-Process -Id $process.Id -Force -ErrorAction Stop },
        [scriptblock]$StartHost,
        [scriptblock]$TestHost,
        [scriptblock]$TestProcessExitedAction = { param($process) Test-CapturedHostProcessExited -Process $process },
        [scriptblock]$DelayAction = { param($milliseconds) Start-Sleep -Milliseconds $milliseconds },
        [int]$HostExitTimeoutMilliseconds = 5000,
        [int]$HostExitPollMilliseconds = 100,
        [int]$HealthStabilityDelayMilliseconds = 250,
        [scriptblock]$MovePathAction = {
            param($source, $destination)
            Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
        },
        [scriptblock]$MutexAction = { param($name, $action) Invoke-WithManagerMutex -Name $name -Action $action }
    )

    if ($null -eq $StartHost) {
        $StartHost = { Start-InstalledHost -InstallPath $InstallPath }.GetNewClosure()
    }
    if ($null -eq $TestHost) {
        $healthContext = [pscustomobject]@{
            InstallPath = $InstallPath
            GetProcessesAction = $GetProcessesAction
            DelayAction = $DelayAction
            StabilityDelayMilliseconds = $HealthStabilityDelayMilliseconds
        }
        $TestHost = {
            Test-InstalledHostStable -InstallPath $healthContext.InstallPath `
                -GetProcessesAction ([scriptblock]$healthContext.GetProcessesAction) `
                -DelayAction ([scriptblock]$healthContext.DelayAction) `
                -StabilityDelayMilliseconds $healthContext.StabilityDelayMilliseconds
        }.GetNewClosure()
    }

    $gate = [pscustomobject]@{ Executed = $false }
    $body = {
        $gate.Executed = $true
        Assert-ManagerStatePathTrusted -StatePath $StatePath
        $processes = @(& $GetProcessesAction)
        $hostProcesses = @($processes | Where-Object { $_.ProcessName -ieq 'QuickBooksServiceHost' })
        $quickBooksRunning = @($processes | Where-Object { $_.ProcessName -ieq 'QBW32' }).Count -gt 0
        $connectorRunning = @($processes | Where-Object { $_.ProcessName -ieq 'QuickBooksConnectorCli' }).Count -gt 0
        $expectedHostPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
        $currentHosts = @($hostProcesses | Where-Object {
            (Get-ServiceHostProcessPath $_) -ieq $expectedHostPath
        })
        $strictHostRunning = $hostProcesses.Count -eq 1 -and $currentHosts.Count -eq 1

        $installedReleaseId = ''
        $installedManifestPath = Join-Path $StatePath 'release.manifest.json'
        if (Test-Path -LiteralPath $installedManifestPath -PathType Leaf) {
            try {
                $installedManifest = Get-Content -Raw -LiteralPath $installedManifestPath | ConvertFrom-Json
                $installedReleaseId = [string]$installedManifest.release_id
            }
            catch { $installedReleaseId = '' }
        }

        $shareAvailable = Test-Path -LiteralPath $SharePath -PathType Container
        $manifestValid = $false
        $availableReleaseId = ''
        $manifest = $null
        if ($shareAvailable) {
            try {
                $manifest = Read-ReleaseManifest -Path (Join-Path $SharePath 'release.manifest.json')
                $manifestValid = $true
                $availableReleaseId = [string]$manifest.release_id
            }
            catch {
                $manifestValid = $false
                $availableReleaseId = ''
            }
        }

        $decision = Get-ManagerDecision -ShareAvailable $shareAvailable -ManifestValid $manifestValid `
            -InstalledReleaseId $installedReleaseId -AvailableReleaseId $availableReleaseId `
            -QuickBooksRunning $quickBooksRunning -ConnectorRunning $connectorRunning `
            -HostRunning $strictHostRunning
        $result = 'ok'
        $rollback = $false
        $transactionOutcome = [pscustomobject]@{
            Result = ''
            RollbackAttempted = $false
            RollbackSucceeded = $false
        }

        $stopContext = [pscustomobject]@{
            GetProcessesAction = $GetProcessesAction
            ExpectedPath = $expectedHostPath
            StopAction = $StopProcessAction
            TestExitedAction = $TestProcessExitedAction
            DelayAction = $DelayAction
            TimeoutMilliseconds = $HostExitTimeoutMilliseconds
            PollMilliseconds = $HostExitPollMilliseconds
        }
        $stopKnownHosts = {
            $latestProcesses = @(& ([scriptblock]$stopContext.GetProcessesAction))
            $latestHosts = @($latestProcesses | Where-Object { $_.ProcessName -ieq 'QuickBooksServiceHost' })
            $latestExpectedHosts = @($latestHosts | Where-Object {
                (Get-ServiceHostProcessPath $_) -ieq $stopContext.ExpectedPath
            })
            foreach ($hostProcess in $latestHosts) {
                & ([scriptblock]$stopContext.StopAction) $hostProcess | Out-Null
            }
            Wait-CapturedHostProcessesExited -Processes $latestExpectedHosts `
                -TestProcessExitedAction ([scriptblock]$stopContext.TestExitedAction) `
                -DelayAction ([scriptblock]$stopContext.DelayAction) `
                -TimeoutMilliseconds $stopContext.TimeoutMilliseconds `
                -PollMilliseconds $stopContext.PollMilliseconds
        }.GetNewClosure()

        $activityContext = [pscustomobject]@{ Action = $GetProcessesAction }
        $testCanStop = {
            $latestProcesses = @(& ([scriptblock]$activityContext.Action))
            $quickBooksStarted = @($latestProcesses | Where-Object { $_.ProcessName -ieq 'QBW32' }).Count -gt 0
            $connectorStarted = @($latestProcesses | Where-Object { $_.ProcessName -ieq 'QuickBooksConnectorCli' }).Count -gt 0
            return (-not $quickBooksStarted -and -not $connectorStarted)
        }.GetNewClosure()

        $primaryFailure = $null
        try {
            switch ($decision) {
                'apply-update' {
                    $transactionResult = Invoke-ReleaseTransaction -Manifest $manifest -SharePath $SharePath `
                        -InstallPath $InstallPath -StatePath $StatePath -StopHost $stopKnownHosts `
                        -StartHost $StartHost -TestHost $TestHost -TestCanStop $testCanStop `
                        -MovePathAction $MovePathAction -Outcome $transactionOutcome
                    if ($transactionResult.Result -eq 'deferred') {
                        $decision = 'defer-update'
                        $result = 'deferred'
                    }
                    else { $result = 'updated' }
                }
                'start-installed' {
                    & $stopKnownHosts
                    & $StartHost | Out-Null
                    if (-not (& $TestHost)) { throw 'Installed host failed its health check.' }
                    $result = 'started'
                }
                'defer-update' { $result = 'deferred' }
                'keep-running' {
                    if (-not (& $TestHost)) { throw 'Installed host failed its stability health check.' }
                    $result = 'running'
                }
            }
            if ($decision -eq 'defer-update') {
                $deferredProcesses = @(& $GetProcessesAction)
                $deferredHosts = @($deferredProcesses | Where-Object { $_.ProcessName -ieq 'QuickBooksServiceHost' })
                $deferredExpectedHosts = @($deferredHosts | Where-Object {
                    (Get-ServiceHostProcessPath $_) -ieq $expectedHostPath
                })
                if ($deferredHosts.Count -eq 0) {
                    & $StartHost | Out-Null
                    if (-not (& $TestHost)) { throw 'Installed host failed its health check.' }
                }
                elseif ($deferredHosts.Count -ne 1 -or $deferredExpectedHosts.Count -ne 1 -or -not (& $TestHost)) {
                    throw 'Installed host failed its deferred stability health check.'
                }
            }
        }
        catch {
            $primaryFailure = $_
            $rollback = [bool]$transactionOutcome.RollbackAttempted
            if ($_.Exception.Data.Contains('RollbackAttempted')) {
                $rollback = $rollback -or [bool]$_.Exception.Data['RollbackAttempted']
            }
            $result = 'failed'
        }

        $logProcesses = @(& ([scriptblock]$activityContext.Action))
        $hostForLog = $logProcesses | Where-Object {
            $_.ProcessName -ieq 'QuickBooksServiceHost' -and
            (Get-ServiceHostProcessPath $_) -ieq $expectedHostPath
        } | Select-Object -First 1

        $managerUpdate = 'skipped'
        $managerRetry = $false
        $managerFailure = $null
        if ($manifestValid) {
            try {
                Update-InstalledManager -Manifest $manifest -SharePath $SharePath -StatePath $StatePath
                $managerUpdate = 'succeeded'
            }
            catch {
                $managerUpdate = 'failed'
                $managerRetry = $true
                $managerFailure = $_
            }
        }

        $logFailure = $null
        try {
            Write-ManagerLog -StatePath $StatePath -Record ([pscustomobject]@{
                installed_release = $installedReleaseId
                available_release = $availableReleaseId
                decision = $decision
                result = $result
                rollback = $rollback
                manager_update = $managerUpdate
                manager_retry = $managerRetry
                host_pid = if ($null -ne $hostForLog) { $hostForLog.Id } else { $null }
                host_path = if ($null -ne $hostForLog) { Get-ServiceHostProcessPath $hostForLog } else { '' }
            })
        }
        catch { $logFailure = $_ }

        if ($null -ne $primaryFailure) {
            if ($null -ne $managerFailure) {
                $primaryFailure.Exception.Data['ManagerUpdateFailure'] = $managerFailure.Exception.Message
            }
            if ($null -ne $logFailure) {
                $primaryFailure.Exception.Data['ManagerLogFailure'] = $logFailure.Exception.Message
            }
            throw $primaryFailure
        }
        if ($null -ne $logFailure) { throw $logFailure }
        return 0
    }.GetNewClosure()

    & $MutexAction 'Global\QuickBooksServiceHostAutoUpdate' $body | Out-Null
    return 0
}

if ($AsLibrary) { return }
Invoke-ServiceHostManager -SharePath $SharePath -InstallPath $InstallPath -StatePath $StatePath
