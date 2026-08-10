[CmdletBinding()]
param(
    [string]$DestinationRoot = '\\PC-VS-APPFS01\CNC Process\COLTON TEST\QBUtility Beta Install',
    [string]$ReleaseId = '',
    [switch]$AsLibrary
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseEntry {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath
    [pscustomobject]@{
        path = $RelativePath.Replace('\', '/')
        length = [long](Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

function New-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][string]$ManagerPath,
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [Parameter(Mandatory = $true)][datetime]$PublishedAtUtc
    )

    $payloadRootPath = (Get-Item -LiteralPath $PayloadRoot).FullName.TrimEnd([char[]]@('\', '/'))
    $files = @(Get-ChildItem -LiteralPath $payloadRootPath -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($payloadRootPath.Length).TrimStart([char[]]@('\', '/'))
        Get-ReleaseEntry -Root $payloadRootPath -RelativePath $relative
    })

    [pscustomobject]@{
        schema_version = 1
        release_id = $ReleaseId
        published_at_utc = $PublishedAtUtc.ToUniversalTime().ToString('o')
        files = $files
        manager = [pscustomobject]@{
            path = 'service_host_manager.ps1'
            length = [long](Get-Item -LiteralPath $ManagerPath).Length
            sha256 = (Get-FileHash -LiteralPath $ManagerPath -Algorithm SHA256).Hash
        }
    }
}

function Copy-SourceIntoPayload {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$PayloadRoot
    )

    $sourceRoot = (Get-Item -LiteralPath $SourcePath).FullName.TrimEnd([char[]]@('\', '/'))
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName) {
        if ($file.Name -ieq 'bridge.settings.psd1' -or
            $file.Name -ieq 'release.manifest.json' -or
            $file.Name -ieq 'release.manifest.json.pending' -or
            $file.Name -ieq 'service_host_manager.ps1') {
            continue
        }

        $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/'))
        $target = Join-Path $PayloadRoot $relative
        $targetParent = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Copy-VerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][scriptblock]$CopyFileAction
    )

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    & $CopyFileAction $Source $Destination

    $sourceItem = Get-Item -LiteralPath $Source
    $destinationItem = Get-Item -LiteralPath $Destination
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ([long]$sourceItem.Length -ne [long]$destinationItem.Length -or $sourceHash -ne $destinationHash) {
        throw "Published file verification failed: $Destination"
    }
}

function Publish-ServiceHostRelease {
    param(
        [Parameter(Mandatory = $true)][string]$HostSourcePath,
        [Parameter(Mandatory = $true)][string]$ConnectorSourcePath,
        [Parameter(Mandatory = $true)][string]$ManagerPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [datetime]$PublishedAtUtc = [datetime]::UtcNow,
        [scriptblock]$CopyFileAction = {
            param([string]$Source, [string]$Destination)
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
        }
    )

    if ([string]::IsNullOrWhiteSpace($ReleaseId)) {
        throw 'Release ID is required.'
    }
    if (-not (Test-Path -LiteralPath $HostSourcePath -PathType Container)) {
        throw "Service host Release output does not exist: $HostSourcePath"
    }
    if (-not (Test-Path -LiteralPath $ConnectorSourcePath -PathType Container)) {
        throw "Connector CLI Release output does not exist: $ConnectorSourcePath"
    }
    if (-not (Test-Path -LiteralPath $ManagerPath -PathType Leaf)) {
        throw "Service host manager does not exist: $ManagerPath"
    }

    $temporaryPayload = Join-Path ([IO.Path]::GetTempPath()) ('quoteaddin-release-' + [guid]::NewGuid().ToString('N'))
    $pendingManifestPath = Join-Path $DestinationPath 'release.manifest.json.pending'
    $finalManifestPath = Join-Path $DestinationPath 'release.manifest.json'
    try {
        New-Item -ItemType Directory -Path $temporaryPayload -Force | Out-Null
        Copy-SourceIntoPayload -SourcePath $ConnectorSourcePath -PayloadRoot $temporaryPayload
        Copy-SourceIntoPayload -SourcePath $HostSourcePath -PayloadRoot $temporaryPayload

        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
        Remove-Item -LiteralPath $pendingManifestPath -Force -ErrorAction SilentlyContinue

        $payloadRootPath = (Get-Item -LiteralPath $temporaryPayload).FullName.TrimEnd([char[]]@('\', '/'))
        foreach ($file in Get-ChildItem -LiteralPath $payloadRootPath -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($payloadRootPath.Length).TrimStart([char[]]@('\', '/'))
            Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $DestinationPath $relative) -CopyFileAction $CopyFileAction
        }

        $publishedManagerPath = Join-Path $DestinationPath 'service_host_manager.ps1'
        Copy-VerifiedFile -Source $ManagerPath -Destination $publishedManagerPath -CopyFileAction $CopyFileAction
        $manifest = New-ReleaseManifest -PayloadRoot $temporaryPayload -ManagerPath $publishedManagerPath `
            -ReleaseId $ReleaseId -PublishedAtUtc $PublishedAtUtc

        $json = $manifest | ConvertTo-Json -Depth 5
        $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
        [IO.File]::WriteAllText($pendingManifestPath, $json, $utf8NoBom)
        Move-Item -LiteralPath $pendingManifestPath -Destination $finalManifestPath -Force
        return $manifest
    }
    finally {
        Remove-Item -LiteralPath $pendingManifestPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $temporaryPayload -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-DefaultReleaseId {
    param([Parameter(Mandatory = $true)][string]$RepositoryPath)

    $commit = $null
    $gitExitCode = 1
    try {
        $gitOutput = @(& git -C $RepositoryPath rev-parse HEAD 2>$null)
        $gitExitCode = $LASTEXITCODE
        $commit = ($gitOutput | Select-Object -First 1)
    }
    catch {
        $commit = $null
        $gitExitCode = 1
    }
    if ($gitExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$commit)) {
        return ([string]$commit).Trim()
    }
    return [datetime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
}

if ($AsLibrary) { return }

$currentDirectory = $PSScriptRoot
$hostSourcePath = Join-Path $currentDirectory 'QuickBooksServiceHost\bin\Release'
$connectorCliSourcePath = Join-Path $currentDirectory 'QuickBooksConnectorCli\bin\Release'
$networkInstallPath = $DestinationRoot
$networkPath = Join-Path $networkInstallPath 'service_host_installation'

New-Item -Path $networkInstallPath, $networkPath -ItemType Directory -Force | Out-Null

# Installer artifacts and operator-owned settings are outside the runtime manifest.
Copy-Item -LiteralPath (Join-Path $currentDirectory 'install_service_host.ps1') -Destination $networkPath -Force
Copy-Item -LiteralPath (Join-Path $currentDirectory 'migrate_legacy_service_host.ps1') -Destination $networkPath -Force

$bridgeSettingsTemplate = Join-Path $currentDirectory 'bridge.settings.template.psd1'
$bridgeSettingsTarget = Join-Path $networkPath 'bridge.settings.psd1'
if (-not (Test-Path -LiteralPath $bridgeSettingsTarget -PathType Leaf)) {
    Copy-Item -LiteralPath $bridgeSettingsTemplate -Destination $bridgeSettingsTarget -Force
    Write-Host 'Seeded bridge.settings.psd1 on the share. Set QB_BRIDGE_TOKEN in it before users install.'
}
else {
    Write-Host 'Existing bridge.settings.psd1 on the share left untouched.'
}

if ([string]::IsNullOrWhiteSpace($ReleaseId)) {
    $ReleaseId = Get-DefaultReleaseId -RepositoryPath $currentDirectory
}

$managerPath = Join-Path $currentDirectory 'service_host_manager.ps1'
Publish-ServiceHostRelease -HostSourcePath $hostSourcePath -ConnectorSourcePath $connectorCliSourcePath `
    -ManagerPath $managerPath -DestinationPath $networkPath -ReleaseId $ReleaseId | Out-Null

Write-Host "Published verified service host release $ReleaseId."
