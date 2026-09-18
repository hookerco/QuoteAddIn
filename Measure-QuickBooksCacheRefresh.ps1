[CmdletBinding()]
param(
    [string]$BuildDirectory,
    [string]$LogDirectory
)

# Run with 32-bit Windows PowerShell in the same interactive user session as QB.
# Read-only queries; no IPC listener, background refresh loop, or company writes.
# Only timing/status/count information is printed. Response records stay in memory.
$ErrorActionPreference = 'Stop'
if (!$BuildDirectory) { $BuildDirectory = Join-Path $PSScriptRoot 'QuickBooksServiceHost\bin\Release' }
if (!$LogDirectory) { $LogDirectory = Join-Path $PSScriptRoot '.test-logs\cache-performance\live' }
if ($PSVersionTable.PSEdition -eq 'Core' -or [Environment]::Is64BitProcess) {
    throw 'Run with C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -File Measure-QuickBooksCacheRefresh.ps1'
}
$previousLogPath = $env:QUOTEADDIN_LOG_PATH
$service = $null
try {
    $buildPath = (Resolve-Path -LiteralPath $BuildDirectory).Path
    $logPath = [IO.Path]::GetFullPath($LogDirectory)
    if ($logPath.StartsWith('\\') -or ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($logPath))).DriveType -eq 'Network') {
        throw 'Choose a local log directory.'
    }
    [IO.Directory]::CreateDirectory($logPath) | Out-Null
    $env:QUOTEADDIN_LOG_PATH = Join-Path $logPath ('refresh-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    foreach ($name in @('QuickBooksIPCContracts.dll', 'QBRequestLibrary.dll', 'QuickBooksConnectorCore.dll', 'QuickBooksIPCService.dll')) {
        [Reflection.Assembly]::LoadFrom((Join-Path $buildPath $name)) | Out-Null
    }
    $factory = New-Object QBRequestLibrary.RequestFactory
    $logger = New-Object QBRequestLibrary.Logger($env:QUOTEADDIN_LOG_PATH)
    $service = New-Object QuickBooksIPCService.QuickBooksService($factory, $logger, $false)
    Add-Type -ReferencedAssemblies @(
        (Join-Path $buildPath 'QuickBooksIPCContracts.dll'),
        (Join-Path $buildPath 'QuickBooksIPCService.dll')
    ) -TypeDefinition @'
public static class CacheTimingWorker
{
    public static QuickBooksIPCContracts.QBStatusResponse<string> Run(QuickBooksIPCService.QuickBooksService service)
    {
        return System.Threading.Tasks.Task.Run(() => service.RefreshQuickBooksCaches()).GetAwaiter().GetResult();
    }
}
'@
    $timer = [Diagnostics.Stopwatch]::StartNew()
    # Match the service host's startup Task.Run (MTA), not PowerShell's STA thread.
    $result = [CacheTimingWorker]::Run($service)
    $timer.Stop()
    $itemCount = if ($result.StatusCode -eq 0) { $service.GetAllItems().Data.Count } else { $null }
    [pscustomobject]@{
        StatusCode = $result.StatusCode; TotalMilliseconds = $timer.ElapsedMilliseconds
        ItemCount = $itemCount; LogPath = $env:QUOTEADDIN_LOG_PATH
    } | Format-List
    Select-String -LiteralPath $env:QUOTEADDIN_LOG_PATH -Pattern 'QB read |QB cache session:' |
        ForEach-Object { $_.Line }
    if ($result.StatusCode -ne 0) { exit 1 }
}
finally {
    if ($null -ne $service) { $service.Dispose() }
    $env:QUOTEADDIN_LOG_PATH = $previousLogPath
}
