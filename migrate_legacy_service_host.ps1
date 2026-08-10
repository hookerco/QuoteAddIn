[CmdletBinding()]
param([string]$TargetUserSid='',[switch]$AsLibrary)

$ErrorActionPreference='Stop'
$legacyTaskName='QuickBooksServiceHost Auto Start and Update'
$environmentNames=@('QUOTE_MODULEV2_QB_CONNECTOR_CLI','QB_BRIDGE_TOKEN','QB_BRIDGE_ORIGIN','QB_BRIDGE_PORT')
$bridgeEnvironmentNames=@('QB_BRIDGE_TOKEN','QB_BRIDGE_ORIGIN','QB_BRIDGE_PORT')

function Test-IsAdministrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ServiceHostProcessPath { param($Process) try{return [string]$Process.Path}catch{return ''} }

function Set-OriginalUserEnvironmentFromMachine {
    param([Parameter(Mandatory=$true)][string]$UserSid,[Parameter(Mandatory=$true)][string[]]$Names)
    $users=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users,[Microsoft.Win32.RegistryView]::Default)
    try {
        $userEnvironment=$users.CreateSubKey("$UserSid\Environment",$true)
        if($null-eq$userEnvironment){throw "The target user registry hive is not loaded: HKEY_USERS\$UserSid"}
        try {
            foreach($name in $Names){
                $machineValue=[Environment]::GetEnvironmentVariable($name,[EnvironmentVariableTarget]::Machine)
                $userValue=$userEnvironment.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if(-not[string]::IsNullOrWhiteSpace([string]$machineValue)-and[string]::IsNullOrWhiteSpace([string]$userValue)){
                    $userEnvironment.SetValue($name,[string]$machineValue,[Microsoft.Win32.RegistryValueKind]::String)
                }
            }
        } finally {$userEnvironment.Dispose()}
    } finally {$users.Dispose()}
}

function Remove-LegacyMachineEnvironment {
    param([Parameter(Mandatory=$true)][string[]]$Names)
    foreach($name in $Names){[Environment]::SetEnvironmentVariable($name,$null,[EnvironmentVariableTarget]::Machine)}
}

function Invoke-LegacyServiceHostCleanup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$LegacyInstallPath,
        [Parameter(Mandatory=$true)][string]$LegacyStatePath,
        [Parameter(Mandatory=$true)][string]$LegacyShortcutPath,
        [Parameter(Mandatory=$true)][string]$TargetUserSid,
        [scriptblock]$GetTaskAction={param($name)Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue},
        [scriptblock]$UnregisterTaskAction={param($name)Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop},
        [scriptblock]$GetProcessesAction={@(Get-Process -Name QuickBooksServiceHost -ErrorAction SilentlyContinue)},
        [scriptblock]$StopProcessAction={param($process)Stop-Process -Id $process.Id -ErrorAction Stop},
        [scriptblock]$DelayAction={param($milliseconds)Start-Sleep -Milliseconds $milliseconds},
        [scriptblock]$MigrateEnvironmentAction={param($sid,$names)Set-OriginalUserEnvironmentFromMachine -UserSid $sid -Names $names},
        [scriptblock]$RemoveMachineEnvironmentAction={param($names)Remove-LegacyMachineEnvironment -Names $names},
        [scriptblock]$RemovePathAction={param($path,$recurse)Remove-Item -LiteralPath $path -Force -Recurse:$recurse -ErrorAction Stop}
    )
    try{$null=New-Object Security.Principal.SecurityIdentifier -ArgumentList $TargetUserSid}
    catch{throw 'Target user SID must be a valid Windows SID.'}
    & $MigrateEnvironmentAction $TargetUserSid $bridgeEnvironmentNames
    & $RemoveMachineEnvironmentAction $environmentNames
    if($null-ne(& $GetTaskAction $legacyTaskName)){& $UnregisterTaskAction $legacyTaskName}
    $legacyHostPath=Join-Path $LegacyInstallPath 'QuickBooksServiceHost.exe'
    foreach($process in @(& $GetProcessesAction)){
        if((Get-ServiceHostProcessPath $process)-ieq$legacyHostPath){& $StopProcessAction $process}
    }
    for($attempt=0;$attempt-lt50-and@(& $GetProcessesAction|Where-Object{(Get-ServiceHostProcessPath $_)-ieq$legacyHostPath}).Count-gt0;$attempt++){& $DelayAction 100}
    if(@(& $GetProcessesAction|Where-Object{(Get-ServiceHostProcessPath $_)-ieq$legacyHostPath}).Count-gt0){throw "Legacy host process did not exit: $legacyHostPath"}
    foreach($entry in @([pscustomobject]@{Path=$LegacyShortcutPath;Recurse=$false},[pscustomobject]@{Path=$LegacyInstallPath;Recurse=$true},[pscustomobject]@{Path=$LegacyStatePath;Recurse=$true})){
        if(Test-Path -LiteralPath $entry.Path){& $RemovePathAction $entry.Path ([bool]$entry.Recurse)}
    }
    [pscustomobject]@{TargetUserSid=$TargetUserSid;TaskName=$legacyTaskName;Phase='Cleaned'}
}

if($AsLibrary){return}
if([string]::IsNullOrWhiteSpace($TargetUserSid)){$TargetUserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
if(-not(Test-IsAdministrator)){
    $scriptPath=if($PSCommandPath){$PSCommandPath}else{$MyInvocation.MyCommand.Path}
    $arguments="-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -TargetUserSid `"$TargetUserSid`""
    Write-Host 'Administrator permission is required once to remove the legacy machine-wide installation.'
    $process=Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}
$legacyInstallPath=Join-Path $env:ProgramFiles 'QuickBooksServiceHost'
$legacyStatePath=Join-Path $env:ProgramData 'QuickBooksServiceHost'
$publicDesktop=[Environment]::GetFolderPath('CommonDesktopDirectory')
$legacyShortcutPath=Join-Path $publicDesktop 'QuickBooksServiceHost.lnk'
$result=Invoke-LegacyServiceHostCleanup -LegacyInstallPath $legacyInstallPath -LegacyStatePath $legacyStatePath -LegacyShortcutPath $legacyShortcutPath -TargetUserSid $TargetUserSid
Write-Host 'Legacy machine-wide Service Host artifacts were removed. Run install_service_host.ps1 as the normal QuickBooks user; it will not start the host automatically.'
