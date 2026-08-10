[CmdletBinding()]
param([ValidateSet('static','cleanup','all')][string]$Scenario='all')
$ErrorActionPreference='Stop'
$scriptPath=Join-Path $PSScriptRoot 'migrate_legacy_service_host.ps1'
if (-not (Test-Path $scriptPath)) { throw 'Legacy migration script is missing.' }
$content=Get-Content -Raw $scriptPath
. $scriptPath -AsLibrary
function Assert-Equal($e,$a,[string]$why){if($e-ne$a){throw "$why. Expected [$e], got [$a]."}}
function Assert-True([bool]$v,[string]$why){if(-not$v){throw $why}}
function Run-Scenario([string]$name){
    if($name-eq'static'){
        Assert-True ($content-match 'Verb\s+RunAs') 'cleanup path must explicitly elevate'
        Assert-True ($content-match 'QuickBooksServiceHost Auto Start and Update') 'cleanup must target the exact legacy task'
        Assert-True ($content-match 'EnvironmentVariableTarget\]::Machine') 'cleanup must remove legacy machine variables'
        Assert-True ($content-match 'HKEY_USERS|RegistryHive\]::Users') 'cleanup must migrate settings to the original user hive'
        Assert-True ($content-notmatch 'Start-Process[\s\S]*QuickBooksServiceHost\.exe') 'cleanup must not launch the host'
        $caught=$null;try{Invoke-LegacyServiceHostCleanup -LegacyInstallPath 'C:\a' -LegacyStatePath 'C:\b' -LegacyShortcutPath 'C:\c' -TargetUserSid 'not-a-sid'}catch{$caught=$_}
        Assert-True ($caught.Exception.Message -match 'valid Windows SID') 'cleanup rejects an invalid target user SID before mutation'
        return
    }
    $root=Join-Path ([IO.Path]::GetTempPath()) ('legacy-cleanup-'+[guid]::NewGuid().ToString('N'))
    $install=Join-Path $root 'Program Files\QuickBooksServiceHost';$state=Join-Path $root 'ProgramData\QuickBooksServiceHost';$shortcut=Join-Path $root 'Public\Desktop\QuickBooksServiceHost.lnk'
    New-Item -ItemType Directory -Force -Path $install,$state,(Split-Path -Parent $shortcut)|Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'QuickBooksServiceHost.exe'),'legacy');[IO.File]::WriteAllText($shortcut,'shortcut')
    $s=[pscustomobject]@{Task=$true;Processes=@([pscustomobject]@{Id=7;ProcessName='QuickBooksServiceHost';Path=(Join-Path $install 'QuickBooksServiceHost.exe')});Migrated=@();RemovedMachine=@();Stopped=0}
    Invoke-LegacyServiceHostCleanup -LegacyInstallPath $install -LegacyStatePath $state -LegacyShortcutPath $shortcut -TargetUserSid 'S-1-5-21-1000' `
      -GetTaskAction {param($n)if($s.Task){[pscustomobject]@{TaskName=$n}}}.GetNewClosure() -UnregisterTaskAction {param($n)$s.Task=$false}.GetNewClosure() `
      -GetProcessesAction {@($s.Processes)}.GetNewClosure() -StopProcessAction {param($p)$s.Stopped++;$s.Processes=@()}.GetNewClosure() -DelayAction {param($m)} `
      -MigrateEnvironmentAction {param($sid,$names)$s.Migrated=@($names)}.GetNewClosure() -RemoveMachineEnvironmentAction {param($names)$s.RemovedMachine=@($names)}.GetNewClosure()
    Assert-Equal $false $s.Task 'legacy task removed';Assert-Equal 1 $s.Stopped 'legacy host stopped once'
    Assert-Equal 3 $s.Migrated.Count 'bridge variables migrated';Assert-True ($s.Migrated -notcontains 'QUOTE_MODULEV2_QB_CONNECTOR_CLI') 'removed legacy connector path is not migrated'
    Assert-Equal 4 $s.RemovedMachine.Count 'all machine variables removed'
    Assert-True (-not(Test-Path $install)) 'Program Files runtime removed';Assert-True (-not(Test-Path $state)) 'ProgramData state removed';Assert-True (-not(Test-Path $shortcut)) 'public shortcut removed'
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
$names=if($Scenario-eq'all'){@('static','cleanup')}else{@($Scenario)};foreach($name in $names){Run-Scenario $name;Write-Output "PASS: $name"}
