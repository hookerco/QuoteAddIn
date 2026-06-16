$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'install_service_host.ps1'
$content = Get-Content -Raw -Path $scriptPath

function Assert-Contains {
    param(
        [string]$Pattern,
        [string]$Message
    )

    if ($content -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [string]$Pattern,
        [string]$Message
    )

    if ($content -match $Pattern) {
        throw $Message
    }
}

Assert-Contains '\$ErrorActionPreference\s*=\s*[''"]Stop[''"]' 'Installer must stop after copy or shortcut failures.'
Assert-Contains 'Test-IsAdministrator' 'Installer must detect whether it is running as administrator.'
Assert-Contains 'Start-Process[\s\S]*-Verb\s+RunAs' 'Installer must relaunch itself elevated when needed.'
Assert-Contains '\$PSScriptRoot' 'Installer must copy from its own folder instead of a hardcoded mapped drive.'
Assert-NotContains 'Z:\\COLTON TEST' 'Installer must not rely on the Z: mapped drive being available after elevation.'

Write-Host 'Install service host script checks passed.'
