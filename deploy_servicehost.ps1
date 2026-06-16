# Define source and network folder locations
$currentDirectory = (Get-Location).Path
$hostSourcePath = "$currentDirectory\QuickBooksServiceHost\bin\Release"
$connectorCliSourcePath = "$currentDirectory\QuickBooksConnectorCli\bin\Release"
$networkInstallPath = "\\PC-VS-APPFS01\CNC Process\COLTON TEST\QBUtility Beta Install"
$networkPath = "$networkInstallPath\service_host_installation"

# Create network folder if it does not exist
if (-not (Test-Path -Path $networkPath)) {
    New-Item -Path $networkPath -ItemType Directory -Force
}

if (-not (Test-Path -Path $hostSourcePath -PathType Container)) {
    throw "Service host Release output does not exist: $hostSourcePath"
}

if (-not (Test-Path -Path $connectorCliSourcePath -PathType Container)) {
    throw "Connector CLI Release output does not exist: $connectorCliSourcePath"
}

# Copy application files to network folder
Copy-Item -Path "$hostSourcePath\*" -Destination $networkPath -Recurse -Force
Copy-Item -Path "$connectorCliSourcePath\*" -Destination $networkPath -Recurse -Force

# Copy the install script to the network folder
$installPSScript = "install_service_host.ps1"
Copy-Item -Path $installPSScript -Destination $networkPath -Force

$installBatScript = "install_service_host.bat"
Copy-Item -Path $installBatScript -Destination $networkInstallPath -Force

Write-Host "Files and install script have been copied to the network folder."
