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

function Copy-VerifiedReleaseToStage {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$SharePath,
        [Parameter(Mandatory = $true)][string]$StagePath
    )

    Remove-Item -LiteralPath $StagePath -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StagePath | Out-Null
    foreach ($entry in @($Manifest.files)) {
        $source = Join-Path $SharePath ([string]$entry.path)
        $target = Join-Path $StagePath ([string]$entry.path)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
        $targetItem = Get-Item -LiteralPath $target
        if ([long]$targetItem.Length -ne [long]$entry.length) {
            throw "Length mismatch: $($entry.path)"
        }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
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
        [Parameter(Mandatory = $true)][string]$StatePath
    )

    if ($null -eq $Manifest.manager) { return }
    $source = Join-Path $SharePath ([string]$Manifest.manager.path)
    $stage = Join-Path $StatePath 'service_host_manager.ps1.stage'
    $target = Join-Path $StatePath 'service_host_manager.ps1'
    Copy-Item -LiteralPath $source -Destination $stage -Force
    try {
        if ([long](Get-Item -LiteralPath $stage).Length -ne [long]$Manifest.manager.length) {
            throw "Length mismatch: $($Manifest.manager.path)"
        }
        $stageHash = (Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash
        if ($stageHash -ne [string]$Manifest.manager.sha256) {
            throw "Hash mismatch: $($Manifest.manager.path)"
        }
        $targetHash = ''
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        }
        if ($targetHash -ne $stageHash) {
            Move-Item -LiteralPath $stage -Destination $target -Force
        }
    }
    finally {
        Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
    }
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
            Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction SilentlyContinue
        },
        [scriptblock]$MovePathAction = {
            param($source, $destination)
            Move-Item -LiteralPath $source -Destination $destination
        }
    )

    New-Item -ItemType Directory -Force -Path $StatePath | Out-Null
    $stagePath = Join-Path $StatePath 'Stage'
    $previousPath = Join-Path $StatePath 'Previous'
    $manifestPath = Join-Path $StatePath 'installed.manifest.json'
    $previousManifest = $null
    $hadManifest = Test-Path -LiteralPath $manifestPath -PathType Leaf
    if ($hadManifest) {
        $previousManifest = Get-Content -Raw -LiteralPath $manifestPath
    }

    Copy-VerifiedReleaseToStage -Manifest $Manifest -SharePath $SharePath -StagePath $stagePath

    if (-not (& $TestCanStop)) {
        & $RemovePathAction $stagePath $true | Out-Null
        return 'deferred'
    }

    $stopCompleted = $false
    $stopAttempted = $false
    $currentMoved = $false
    $stagePromoted = $false
    try {
        $stopAttempted = $true
        & $StopHost
        $stopCompleted = $true
        & $RemovePathAction $previousPath $true
        if (Test-Path -LiteralPath $InstallPath -PathType Container) {
            & $MovePathAction $InstallPath $previousPath
            $currentMoved = $true
        }
        & $MovePathAction $stagePath $InstallPath
        $stagePromoted = $true
        $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath
        & $StartHost
        if (-not (& $TestHost)) {
            throw 'Updated host failed its health check.'
        }
    }
    catch {
        if ($stagePromoted) {
            & $RemovePathAction $InstallPath $true
            if (Test-Path -LiteralPath $previousPath -PathType Container) {
                & $MovePathAction $previousPath $InstallPath
            }
            if ($hadManifest) {
                Set-Content -LiteralPath $manifestPath -Value $previousManifest -NoNewline
            }
            else {
                & $RemovePathAction $manifestPath $false
            }
            if (Test-Path -LiteralPath $InstallPath -PathType Container) {
                & $StartHost
                if (-not (& $TestHost)) {
                    throw 'Release failed and previous host failed to restart.'
                }
                Update-InstalledManager -Manifest $Manifest -SharePath $SharePath -StatePath $StatePath
                throw 'Release failed; previous host restored.'
            }
        }
        elseif ($currentMoved) {
            if (Test-Path -LiteralPath $previousPath -PathType Container) {
                & $MovePathAction $previousPath $InstallPath
            }
            if (Test-Path -LiteralPath $InstallPath -PathType Container) {
                & $StartHost
                if (-not (& $TestHost)) {
                    throw 'Release failed and previous host failed to restart.'
                }
            }
        }
        elseif ($stopAttempted -and (Test-Path -LiteralPath $InstallPath -PathType Container)) {
            & $StartHost
            if (-not (& $TestHost)) {
                throw 'Release failed and installed host failed to restart.'
            }
        }
        throw
    }

    Update-InstalledManager -Manifest $Manifest -SharePath $SharePath -StatePath $StatePath
    return 'updated'
}

function Rotate-ManagerLogs {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [long]$IncomingLength = 0
    )

    $logPath = Join-Path $StatePath 'service-host-manager.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) { return }
    if (([long](Get-Item -LiteralPath $logPath).Length + $IncomingLength) -le 1MB) { return }

    Remove-Item -LiteralPath "$logPath.5" -Force -ErrorAction SilentlyContinue
    foreach ($index in 4..1) {
        $source = "$logPath.$index"
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Move-Item -LiteralPath $source -Destination "$logPath.$($index + 1)" -Force
        }
    }
    Move-Item -LiteralPath $logPath -Destination "$logPath.1" -Force
}

function Write-ManagerLog {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)]$Record
    )

    New-Item -ItemType Directory -Force -Path $StatePath | Out-Null
    $safeRecord = [ordered]@{
        timestamp_utc = [datetime]::UtcNow.ToString('o')
        installed_release = [string]$Record.installed_release
        available_release = [string]$Record.available_release
        decision = [string]$Record.decision
        result = [string]$Record.result
        rollback = [bool]$Record.rollback
        host_pid = $Record.host_pid
        host_path = [string]$Record.host_path
    }
    $line = $safeRecord | ConvertTo-Json -Compress
    $incomingLength = [Text.Encoding]::UTF8.GetByteCount($line + [Environment]::NewLine)
    Rotate-ManagerLogs -StatePath $StatePath -IncomingLength $incomingLength
    $utf8NoBom = New-Object Text.UTF8Encoding -ArgumentList $false
    [IO.File]::AppendAllText((Join-Path $StatePath 'service-host-manager.log'), `
        $line + [Environment]::NewLine, $utf8NoBom)
}

function Get-ServiceHostProcessPath {
    param($Process)
    try { return [string]$Process.Path }
    catch { return '' }
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
        [scriptblock]$MutexAction = { param($name, $action) Invoke-WithManagerMutex -Name $name -Action $action }
    )

    New-Item -ItemType Directory -Force -Path $StatePath | Out-Null
    if ($null -eq $StartHost) {
        $StartHost = { Start-InstalledHost -InstallPath $InstallPath }.GetNewClosure()
    }
    if ($null -eq $TestHost) {
        $TestHost = {
            $expected = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
            foreach ($process in @(Get-Process -Name 'QuickBooksServiceHost' -ErrorAction SilentlyContinue)) {
                if ((Get-ServiceHostProcessPath $process) -ieq $expected) { return $true }
            }
            return $false
        }.GetNewClosure()
    }

    $gate = [pscustomobject]@{ Executed = $false }
    $body = {
        $gate.Executed = $true
        $processes = @(& $GetProcessesAction)
        $hostProcesses = @($processes | Where-Object { $_.ProcessName -ieq 'QuickBooksServiceHost' })
        $quickBooksRunning = @($processes | Where-Object { $_.ProcessName -ieq 'QBW32' }).Count -gt 0
        $connectorRunning = @($processes | Where-Object { $_.ProcessName -ieq 'QuickBooksConnectorCli' }).Count -gt 0
        $expectedHostPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
        $currentHosts = @($hostProcesses | Where-Object {
            (Get-ServiceHostProcessPath $_) -ieq $expectedHostPath
        })

        $installedReleaseId = ''
        $installedManifestPath = Join-Path $StatePath 'installed.manifest.json'
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
            -HostRunning ($currentHosts.Count -gt 0)
        $result = 'ok'
        $rollback = $false

        $stopContext = [pscustomobject]@{
            Processes = $hostProcesses
            Action = $StopProcessAction
        }
        $stopKnownHosts = {
            foreach ($hostProcess in @($stopContext.Processes)) {
                & ([scriptblock]$stopContext.Action) $hostProcess | Out-Null
            }
        }.GetNewClosure()

        $activityContext = [pscustomobject]@{
            Action = $GetProcessesAction
        }
        $testCanStop = {
            $latestProcesses = @(& ([scriptblock]$activityContext.Action))
            $quickBooksStarted = @($latestProcesses | Where-Object {
                $_.ProcessName -ieq 'QBW32'
            }).Count -gt 0
            $connectorStarted = @($latestProcesses | Where-Object {
                $_.ProcessName -ieq 'QuickBooksConnectorCli'
            }).Count -gt 0
            return (-not $quickBooksStarted -and -not $connectorStarted)
        }.GetNewClosure()

        try {
            switch ($decision) {
                'apply-update' {
                    $transactionResult = Invoke-ReleaseTransaction -Manifest $manifest -SharePath $SharePath `
                        -InstallPath $InstallPath -StatePath $StatePath -StopHost $stopKnownHosts `
                        -StartHost $StartHost -TestHost $TestHost -TestCanStop $testCanStop
                    if ($transactionResult -eq 'deferred') {
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
                'keep-running' { $result = 'running' }
            }
        }
        catch {
            if ($_.Exception.Message -like 'Release failed;*') { $rollback = $true }
            $result = 'failed'
            throw
        }
        finally {
            $logProcesses = @(& ([scriptblock]$activityContext.Action))
            $hostForLog = $logProcesses | Where-Object {
                $_.ProcessName -ieq 'QuickBooksServiceHost' -and
                (Get-ServiceHostProcessPath $_) -ieq $expectedHostPath
            } | Select-Object -First 1
            if ($manifestValid) {
                Update-InstalledManager -Manifest $manifest -SharePath $SharePath -StatePath $StatePath
            }
            Write-ManagerLog -StatePath $StatePath -Record ([pscustomobject]@{
                installed_release = $installedReleaseId
                available_release = $availableReleaseId
                decision = $decision
                result = $result
                rollback = $rollback
                host_pid = if ($null -ne $hostForLog) { $hostForLog.Id } else { $null }
                host_path = if ($null -ne $hostForLog) { Get-ServiceHostProcessPath $hostForLog } else { '' }
            })
        }
        return 0
    }.GetNewClosure()

    & $MutexAction 'Global\QuickBooksServiceHostAutoUpdate' $body | Out-Null
    if (-not $gate.Executed) {
        Write-ManagerLog -StatePath $StatePath -Record ([pscustomobject]@{
            installed_release = ''
            available_release = ''
            decision = 'overlap_skipped'
            result = 'skipped'
            rollback = $false
            host_pid = $null
            host_path = ''
        })
    }
    return 0
}

if ($AsLibrary) { return }
Invoke-ServiceHostManager -SharePath $SharePath -InstallPath $InstallPath -StatePath $StatePath
