$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-InstallScriptPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -match '^[A-Za-z]:\\') {
        $driveName = $fullPath.Substring(0, 1)
        $drive = Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue

        if ($drive -and $drive.DisplayRoot) {
            $relativePath = $fullPath.Substring(2).TrimStart('\')
            return (Join-Path $drive.DisplayRoot $relativePath)
        }
    }

    return $fullPath
}

$scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }

if (-not (Test-IsAdministrator)) {
    $elevatedScriptPath = Resolve-InstallScriptPath -Path $scriptPath
    $arguments = "-NoProfile -ExecutionPolicy Unrestricted -File `"$elevatedScriptPath`""

    Write-Host 'Administrator permission is required. Relaunching installer...'
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru -ErrorAction Stop

    if ($null -ne $process.ExitCode) {
        exit $process.ExitCode
    }

    exit 0
}

$sourcePath = $PSScriptRoot.Trim()
$destinationPath = (Join-Path $env:ProgramFiles 'QuickBooksServiceHost').Trim()
$targetPath = (Join-Path $destinationPath 'QuickBooksServiceHost.exe').Trim()
$connectorCliTargetPath = (Join-Path $destinationPath 'QuickBooksConnectorCli.exe').Trim()

if (-not (Test-Path -Path $sourcePath -PathType Container)) {
    throw "Source path does not exist: $sourcePath"
}

if (-not (Test-Path -Path $destinationPath -PathType Container)) {
    New-Item -Path $destinationPath -ItemType Directory -Force | Out-Null
}

Copy-Item -Path (Join-Path $sourcePath '*') -Destination $destinationPath -Recurse -Force -ErrorAction Stop

if (-not (Test-Path -Path $targetPath -PathType Leaf)) {
    throw "Installed executable was not found: $targetPath"
}

if (-not (Test-Path -Path $connectorCliTargetPath -PathType Leaf)) {
    throw "Installed connector CLI was not found: $connectorCliTargetPath"
}

[Environment]::SetEnvironmentVariable(
    'QUOTE_MODULEV2_QB_CONNECTOR_CLI',
    $connectorCliTargetPath,
    [EnvironmentVariableTarget]::Machine)

$shortcutName = 'QuickBooksServiceHost.lnk'
$desktopPath = [Environment]::GetFolderPath('CommonDesktopDirectory')
if ([string]::IsNullOrWhiteSpace($desktopPath)) {
    $desktopPath = Join-Path $env:Public 'Desktop'
}

$shortcutPath = (Join-Path $desktopPath $shortcutName).Trim()
$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $destinationPath
$shortcut.WindowStyle = 1
$shortcut.Save()

Write-Output "Destination Path: $destinationPath"
Write-Output "Source Path: $sourcePath"
Write-Output "Target Path: $targetPath"
Write-Output "Connector CLI Path: $connectorCliTargetPath"
Write-Output "Shortcut Path: $shortcutPath"
Write-Host 'Installation complete. A shortcut has been created on the desktop.'
