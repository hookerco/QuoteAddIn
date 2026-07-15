[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Scenario)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'service_host_manager.ps1') -AsLibrary

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) {
        throw "$Because. Expected [$Expected], got [$Actual]."
    }
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
    'manifest-rejects-hash'
)

if ($Scenario -eq 'all') {
    foreach ($scenarioName in $allScenarios) {
        Run-Scenario -Name $scenarioName
    }
}
else {
    Run-Scenario -Name $Scenario
}
