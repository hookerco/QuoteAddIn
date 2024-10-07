$sourcePath = "Z:\COLTON TEST\QBUtility Beta Install\service_host_installation".Trim()
$destinationPath = "$env:ProgramFiles\QuickBooksServiceHost".Trim()

# Copy files from source to destination
if (-not (Test-Path -Path $destinationPath)) {
    New-Item -Path $destinationPath -ItemType Directory -Force
}
Copy-Item -Path "$sourcePath\*" -Destination $destinationPath -Recurse -Force

# Define shortcut properties
$shortcutName = "QuickBooksServiceHost.lnk"
$shortcutPath = "$env:Public\Desktop\$shortcutName".Trim()
$targetPath = "$destinationPath\QuickBooksServiceHost.exe".Trim()
$wshShell = New-Object -ComObject WScript.Shell

# Create desktop shortcut
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $destinationPath
$shortcut.WindowStyle = 1
$shortcut.Save()

Write-Output "Destination Path: $destinationPath"
Write-Output "Source Path: $sourcePath"
Write-Output "Target Path: $targetPath"


Write-Host "Installation complete. A shortcut has been created on the desktop."