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

if ($AsLibrary) { return }
Invoke-ServiceHostManager -SharePath $SharePath -InstallPath $InstallPath -StatePath $StatePath
