[CmdletBinding()]
param([switch]$AsLibrary)

$ErrorActionPreference = 'Stop'

function Test-SafeReleasePath {
    param([Parameter(Mandatory=$true)][string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return $false }
    $parts = $Path.Replace('/','\').Split([char]'\')
    if ($parts.Count -eq 0) { return $false }
    foreach ($part in $parts) {
        if ($part -eq '.' -or $part -eq '..' -or [string]::IsNullOrWhiteSpace($part) -or
            $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
    }
    return $true
}

function Test-ServiceHostReleaseManifest {
    param([Parameter(Mandatory=$true)]$Manifest)
    if ([int]$Manifest.schema_version -ne 1 -or [string]::IsNullOrWhiteSpace([string]$Manifest.release_id)) {
        throw 'Unsupported or incomplete release manifest.'
    }
    $seen = @{}
    foreach ($entry in @($Manifest.files)) {
        $relative = [string]$entry.path
        if (-not (Test-SafeReleasePath $relative) -or [long]$entry.length -lt 0 -or [string]$entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "Invalid release manifest entry: $relative"
        }
        $key = $relative.Replace('\','/').ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw "Duplicate release manifest entry: $relative" }
        $seen[$key] = $true
    }
    foreach ($required in @('quickbooksservicehost.exe','quickbooksconnectorcli.exe')) {
        if (-not $seen.ContainsKey($required)) { throw "Release manifest is missing required file: $required" }
    }
    $manager = $Manifest.manager
    if ($null -eq $manager -or [string]$manager.path -ne 'service_host_manager.ps1' -or
        [long]$manager.length -lt 0 -or [string]$manager.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Release manifest has an invalid manager entry.'
    }
    return $true
}

function Get-ServiceHostProcessPath {
    param($Process)
    try { return [string]$Process.Path } catch { return '' }
}

function Invoke-WithInstallerMutex {
    param([Parameter(Mandatory=$true)][string]$Name,[Parameter(Mandatory=$true)][scriptblock]$Action)
    $mutex = New-Object Threading.Mutex($false,$Name)
    $acquired = $false
    try {
        $acquired = $mutex.WaitOne(30000)
        if (-not $acquired) { throw 'Timed out waiting for another service host installation.' }
        & $Action
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Invoke-ServiceHostInstall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$SourcePath,
        [Parameter(Mandatory=$true)][string]$InstallPath,
        [Parameter(Mandatory=$true)][string]$StatePath,
        [Parameter(Mandatory=$true)][string]$CurrentUserDesktopPath,
        [scriptblock]$SetEnvironmentVariableAction = { param($name,$value) [Environment]::SetEnvironmentVariable($name,$value,[EnvironmentVariableTarget]::User) },
        [scriptblock]$CreateShortcutAction = {
            param($shortcutPath,$targetPath,$workingDirectory)
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $targetPath
            $shortcut.WorkingDirectory = $workingDirectory
            $shortcut.Save()
        },
        [scriptblock]$GetProcessesAction = { @(Get-Process -Name QuickBooksServiceHost -ErrorAction SilentlyContinue) },
        [scriptblock]$StopProcessAction = { param($process) Stop-Process -Id $process.Id -ErrorAction Stop },
        [scriptblock]$DelayAction = { param($milliseconds) Start-Sleep -Milliseconds $milliseconds },
        [scriptblock]$EnsureDirectoryAction = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null },
        [scriptblock]$CopyFileAction = { param($source,$destination) Copy-Item -LiteralPath $source -Destination $destination -Force },
        [scriptblock]$MovePathAction = { param($source,$destination) Move-Item -LiteralPath $source -Destination $destination -Force },
        [scriptblock]$RemovePathAction = { param($path,$recurse) Remove-Item -LiteralPath $path -Force -Recurse:$recurse -ErrorAction SilentlyContinue },
        [scriptblock]$MutexAction = { param($name,$action) Invoke-WithInstallerMutex -Name $name -Action $action }
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) { throw "Source path does not exist: $SourcePath" }
    if ([string]::IsNullOrWhiteSpace($InstallPath) -or [string]::IsNullOrWhiteSpace($StatePath) -or [string]::IsNullOrWhiteSpace($CurrentUserDesktopPath)) {
        throw 'Install, state, and current-user desktop paths are required.'
    }

    $manifestPath = Join-Path $SourcePath 'release.manifest.json'
    $manifestBytesBefore = [IO.File]::ReadAllBytes($manifestPath)
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytesBefore) | ConvertFrom-Json
    Test-ServiceHostReleaseManifest $manifest | Out-Null
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne
        ([BitConverter]::ToString(([Security.Cryptography.SHA256]::Create().ComputeHash($manifestBytesBefore))).Replace('-',''))) {
        throw 'Release manifest changed during validation.'
    }

    & $EnsureDirectoryAction $StatePath
    $transactionsPath = Join-Path $StatePath 'Transactions'
    & $EnsureDirectoryAction $transactionsPath
    $stageRoot = Join-Path $transactionsPath ([guid]::NewGuid().ToString('N'))
    $stagePayload = Join-Path $stageRoot 'Payload'
    $stageManager = Join-Path $stageRoot 'Manager\service_host_manager.ps1'
    $stageManifest = Join-Path $stageRoot 'release.manifest.json'
    & $EnsureDirectoryAction $stagePayload
    & $EnsureDirectoryAction (Split-Path -Parent $stageManager)

    try {
        foreach ($entry in @($manifest.files)) {
            $source = Join-Path $SourcePath ([string]$entry.path)
            $target = Join-Path $stagePayload ([string]$entry.path)
            & $EnsureDirectoryAction (Split-Path -Parent $target)
            & $CopyFileAction $source $target
            if ([long](Get-Item -LiteralPath $target).Length -ne [long]$entry.length -or
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
                throw "Staged file verification failed: $($entry.path)"
            }
        }
        $managerSource = Join-Path $SourcePath ([string]$manifest.manager.path)
        & $CopyFileAction $managerSource $stageManager
        if ([long](Get-Item -LiteralPath $stageManager).Length -ne [long]$manifest.manager.length -or
            (Get-FileHash -LiteralPath $stageManager -Algorithm SHA256).Hash -ne [string]$manifest.manager.sha256) {
            throw 'Staged manager verification failed: service_host_manager.ps1'
        }
        [IO.File]::WriteAllBytes($stageManifest,$manifestBytesBefore)

        $bridge = @{ QB_BRIDGE_TOKEN=''; QB_BRIDGE_ORIGIN='http://APPSRV01:8742'; QB_BRIDGE_PORT='8788' }
        $bridgeSettingsPath = Join-Path $SourcePath 'bridge.settings.psd1'
        if (Test-Path -LiteralPath $bridgeSettingsPath -PathType Leaf) {
            $loaded = Import-PowerShellDataFile -LiteralPath $bridgeSettingsPath
            foreach ($key in @($bridge.Keys)) {
                if ($loaded.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$loaded[$key])) { $bridge[$key]=[string]$loaded[$key] }
            }
        } else { Write-Warning 'bridge.settings.psd1 was not found next to the installer; using defaults (blank token).' }

        $hostPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
        $connectorPath = Join-Path $InstallPath 'QuickBooksConnectorCli.exe'
        $managerTarget = Join-Path $StatePath 'Manager\service_host_manager.ps1'
        $installedManifest = Join-Path $StatePath 'release.manifest.json'
        $previous = Join-Path $StatePath 'Previous'
        $previousPayload = Join-Path $previous 'Payload'
        $previousManager = Join-Path $previous 'Manager\service_host_manager.ps1'
        $previousManifest = Join-Path $previous 'release.manifest.json'

        $mutation = {
            $previousPrepared = $false
            $currentMoved = $false
            $candidatePromoted = $false
            try {
                $allHosts = @(& $GetProcessesAction)
                foreach ($process in $allHosts) {
                    $processPath = try { [string]$process.Path } catch { '' }
                    if ($processPath -ine $hostPath) {
                        throw "A service host is running outside the current-user install path. Run migrate_legacy_service_host.ps1 first: $processPath"
                    }
                    & $StopProcessAction $process
                }
                for ($attempt=0; $attempt -lt 50 -and @(& $GetProcessesAction).Count -gt 0; $attempt++) { & $DelayAction 100 }
                if (@(& $GetProcessesAction).Count -gt 0) { throw "Installed host process did not exit: $hostPath" }

                if (Test-Path -LiteralPath $previous) { & $RemovePathAction $previous $true }
                & $EnsureDirectoryAction (Split-Path -Parent $previousManager)
                if (Test-Path -LiteralPath $managerTarget -PathType Leaf) { & $CopyFileAction $managerTarget $previousManager }
                if (Test-Path -LiteralPath $installedManifest -PathType Leaf) { & $CopyFileAction $installedManifest $previousManifest }
                $previousPrepared = $true
                if (Test-Path -LiteralPath $InstallPath -PathType Container) {
                    & $MovePathAction $InstallPath $previousPayload
                    $currentMoved = $true
                }
                & $EnsureDirectoryAction (Split-Path -Parent $InstallPath)
                & $MovePathAction $stagePayload $InstallPath
                $candidatePromoted = $true
                if (Test-Path -LiteralPath $managerTarget) { & $RemovePathAction $managerTarget $false }
                & $EnsureDirectoryAction (Split-Path -Parent $managerTarget)
                & $MovePathAction $stageManager $managerTarget
                if (Test-Path -LiteralPath $installedManifest) { & $RemovePathAction $installedManifest $false }
                & $MovePathAction $stageManifest $installedManifest

                foreach ($entry in @($manifest.files)) {
                    $target = Join-Path $InstallPath ([string]$entry.path)
                    if ([long](Get-Item -LiteralPath $target).Length -ne [long]$entry.length -or
                        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
                        throw "Installed file verification failed: $($entry.path)"
                    }
                }
                & $SetEnvironmentVariableAction 'QUOTE_MODULEV2_QB_CONNECTOR_CLI' $connectorPath
                foreach ($key in @('QB_BRIDGE_TOKEN','QB_BRIDGE_ORIGIN','QB_BRIDGE_PORT')) { & $SetEnvironmentVariableAction $key $bridge[$key] }
                & $CreateShortcutAction (Join-Path $CurrentUserDesktopPath 'QuickBooksServiceHost.lnk') $hostPath $InstallPath
            }
            catch {
                $failure = $_
                try {
                    if ($candidatePromoted -and (Test-Path -LiteralPath $InstallPath)) { & $RemovePathAction $InstallPath $true }
                    if ($currentMoved -and (Test-Path -LiteralPath $previousPayload)) { & $MovePathAction $previousPayload $InstallPath }
                    if ($previousPrepared) {
                        if (Test-Path -LiteralPath $managerTarget) { & $RemovePathAction $managerTarget $false }
                        if (Test-Path -LiteralPath $previousManager) { & $EnsureDirectoryAction (Split-Path -Parent $managerTarget); & $MovePathAction $previousManager $managerTarget }
                        if (Test-Path -LiteralPath $installedManifest) { & $RemovePathAction $installedManifest $false }
                        if (Test-Path -LiteralPath $previousManifest) { & $MovePathAction $previousManifest $installedManifest }
                    }
                } catch { $failure.Exception.Data['RollbackFailure']=$_.Exception.Message }
                throw $failure
            }
        }.GetNewClosure()
        & $MutexAction 'Local\QuickBooksServiceHostInstall' $mutation | Out-Null

        if ([string]::IsNullOrWhiteSpace($bridge.QB_BRIDGE_TOKEN)) {
            Write-Warning 'QB_BRIDGE_TOKEN is blank - the QuickBooks bridge will reject all requests with 403 until settings are supplied and the host is started manually.'
        }
        return [pscustomobject]@{ HostPath=$hostPath; ConnectorPath=$connectorPath; ManagerPath=$managerTarget; ShortcutPath=(Join-Path $CurrentUserDesktopPath 'QuickBooksServiceHost.lnk'); Phase='Installed' }
    }
    finally {
        if (Test-Path -LiteralPath $stageRoot) { & $RemovePathAction $stageRoot $true }
    }
}

if ($AsLibrary) { return }

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$destinationPath = Join-Path (Join-Path $localAppData 'Programs') 'QuickBooksServiceHost'
$statePath = Join-Path $localAppData 'QuickBooksServiceHost'
$desktopPath = [Environment]::GetFolderPath('DesktopDirectory')
$result = Invoke-ServiceHostInstall -SourcePath $PSScriptRoot.Trim() -InstallPath $destinationPath -StatePath $statePath -CurrentUserDesktopPath $desktopPath
Write-Output "Destination Path: $destinationPath"
Write-Output "Target Path: $($result.HostPath)"
Write-Output "Connector CLI Path: $($result.ConnectorPath)"
Write-Output "Manager Path: $($result.ManagerPath)"
Write-Output "Shortcut Path: $($result.ShortcutPath)"
Write-Host 'Installation complete. Start the QuickBooks Service Host manually from the current-user shortcut when QuickBooks is running as this same normal user.'
