[CmdletBinding()]
param(
    [string]$InteractiveUser = '',
    [string]$InteractiveSid = '',
    [switch]$AsLibrary
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-InteractiveInstallIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    [pscustomobject]@{ Name = $identity.Name; Sid = $identity.User.Value }
}

function Resolve-InteractiveUserSid {
    param([Parameter(Mandatory = $true)][string]$InteractiveUser)
    $account = New-Object Security.Principal.NTAccount $InteractiveUser
    return $account.Translate([Security.Principal.SecurityIdentifier]).Value
}

function Assert-ElevatedIdentityMatches {
    param(
        [Parameter(Mandatory = $true)][string]$InteractiveUser,
        [Parameter(Mandatory = $true)][string]$InteractiveSid,
        [scriptblock]$GetCurrentIdentityAction = { Get-InteractiveInstallIdentity },
        [scriptblock]$ResolveInteractiveUserSidAction = { param($name) Resolve-InteractiveUserSid -InteractiveUser $name },
        [scriptblock]$TestAdministratorAction = { Test-IsAdministrator }
    )
    $current = & $GetCurrentIdentityAction
    if ([string]$current.Sid -ne $InteractiveSid -or -not (& $TestAdministratorAction)) {
        throw 'Installer elevation must use the same local-administrator account that runs QuickBooks.'
    }
    $resolvedSid = & $ResolveInteractiveUserSidAction $InteractiveUser
    if ([string]$resolvedSid -ne $InteractiveSid) {
        throw 'InteractiveUser must resolve to the verified InteractiveSid.'
    }
}

function New-ServiceHostSecureAcl {
    param([Parameter(Mandatory = $true)][bool]$IsContainer)

    $acl = if ($IsContainer) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else {
        New-Object Security.AccessControl.FileSecurity
    }
    $administrators = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $system = New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = if ($IsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($sid in @($system, $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Test-ServiceHostSecureAcl {
    param(
        [Parameter(Mandatory = $true)]$Acl,
        [Parameter(Mandatory = $true)][bool]$IsContainer
    )

    $administratorsSid = 'S-1-5-32-544'
    $allowedSids = @('S-1-5-18', $administratorsSid)
    try { $owner = $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value }
    catch { return $false }
    if ($owner -ne $administratorsSid -or -not $Acl.AreAccessRulesProtected) { return $false }

    $expectedInheritance = if ($IsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    $rules = @($Acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) { return $false }
    $actualSids = @()
    foreach ($rule in $rules) {
        $actualSids += $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            $rule.IsInherited) {
            return $false
        }
    }
    return (($actualSids | Sort-Object) -join '|') -eq (($allowedSids | Sort-Object) -join '|')
}

function Assert-ServiceHostStateItemSafe {
    param([Parameter(Mandatory = $true)]$Item)
    if ($null -eq $Item) { throw 'A required service host state path does not exist.' }
    if (([long]$Item.Attributes -band [long][IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed in the service host state tree: $($Item.FullName)"
    }
}

function Protect-ServiceHostStateTree {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [scriptblock]$EnsureDirectoryAction = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null },
        [scriptblock]$GetItemAction = { param($path) Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue },
        [scriptblock]$GetChildrenAction = { param($path) @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) },
        [scriptblock]$GetAclAction = { param($path) Get-Acl -LiteralPath $path },
        [scriptblock]$SetOwnerAction = {
            param($path, $ownerSid)
            $ownershipAcl = Get-Acl -LiteralPath $path
            $ownershipAcl.SetOwner($ownerSid)
            Set-Acl -LiteralPath $path -AclObject $ownershipAcl
        },
        [scriptblock]$SetAclAction = { param($path, $acl) Set-Acl -LiteralPath $path -AclObject $acl }
    )

    $protectEntry = {
        param($item)
        $path = [string]$item.FullName
        $freshItem = & $GetItemAction $path
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $administrators = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
        & $SetOwnerAction $path $administrators | Out-Null

        $freshItem = & $GetItemAction $path
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $ownerAcl = & $GetAclAction $path
        if ($ownerAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $administrators.Value) {
            throw "Service host state owner verification failed: $path"
        }

        $freshItem = & $GetItemAction $path
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $secureAcl = New-ServiceHostSecureAcl -IsContainer ([bool]$freshItem.PSIsContainer)
        & $SetAclAction $path $secureAcl | Out-Null

        $freshItem = & $GetItemAction $path
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $verifiedAcl = & $GetAclAction $path
        if (-not (Test-ServiceHostSecureAcl -Acl $verifiedAcl -IsContainer ([bool]$freshItem.PSIsContainer))) {
            throw "Service host state ACL verification failed: $path"
        }
    }.GetNewClosure()

    $rootItem = & $GetItemAction $StatePath
    if ($null -eq $rootItem) {
        & $EnsureDirectoryAction $StatePath
        $rootItem = & $GetItemAction $StatePath
    }
    if ($null -eq $rootItem -or -not [bool]$rootItem.PSIsContainer) { throw "State path is not a directory: $StatePath" }
    & $protectEntry $rootItem

    foreach ($name in @('Manager', 'Logs')) {
        $requiredPath = Join-Path $StatePath $name
        $requiredItem = & $GetItemAction $requiredPath
        if ($null -eq $requiredItem) {
            & $EnsureDirectoryAction $requiredPath
            $requiredItem = & $GetItemAction $requiredPath
        }
        if ($null -eq $requiredItem -or -not [bool]$requiredItem.PSIsContainer) { throw "State path is not a directory: $requiredPath" }
        & $protectEntry $requiredItem
    }

    $queue = New-Object 'Collections.Generic.Queue[object]'
    $queue.Enqueue($rootItem)
    $visited = @{}
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        $key = ([string]$directory.FullName).ToLowerInvariant()
        if ($visited.ContainsKey($key)) { continue }
        $visited[$key] = $true
        $freshDirectory = & $GetItemAction $directory.FullName
        Assert-ServiceHostStateItemSafe -Item $freshDirectory
        foreach ($child in @(& $GetChildrenAction $freshDirectory.FullName)) {
            Assert-ServiceHostStateItemSafe -Item $child
            & $protectEntry $child
            if ([bool]$child.PSIsContainer) { $queue.Enqueue($child) }
        }
    }
    return @($visited.Keys)
}

function Invoke-WithInstallerMutex {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$WaitMilliseconds,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $mutex = New-Object Threading.Mutex -ArgumentList $false, $Name
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne($WaitMilliseconds) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw "Timed out waiting for service host update lock: $Name" }
        return (& $Action)
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function New-ServiceHostTaskPlan {
    param(
        [Parameter(Mandatory = $true)][string]$InteractiveUser,
        [Parameter(Mandatory = $true)][string]$ManagerPath
    )
    [pscustomobject]@{
        TaskName = 'QuickBooksServiceHost Auto Start and Update'
        UserId = $InteractiveUser
        RunLevel = 'Highest'
        LogonType = 'Interactive'
        DailyAt = '06:00:00'
        AtLogOn = $true
        StartWhenAvailable = $true
        WakeToRun = $false
        MultipleInstances = 'IgnoreNew'
        Execute = 'powershell.exe'
        Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ManagerPath`""
        ManagerPath = $ManagerPath
    }
}

function Register-ServiceHostTask {
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [scriptblock]$RegisterTaskAction
    )
    if ($null -ne $RegisterTaskAction) { & $RegisterTaskAction $Plan; return }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$($Plan.ManagerPath)`""
    $triggers = @(
        New-ScheduledTaskTrigger -AtLogOn -User $Plan.UserId
        New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.AddHours(6))
    )
    $principal = New-ScheduledTaskPrincipal -UserId $Plan.UserId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    $settings.WakeToRun = $false
    Register-ScheduledTask -TaskName $Plan.TaskName -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null
}

function Resolve-InstallScriptPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -match '^[A-Za-z]:\\') {
        $drive = Get-PSDrive -Name $fullPath.Substring(0, 1) -ErrorAction SilentlyContinue
        if ($drive -and $drive.DisplayRoot) { return Join-Path $drive.DisplayRoot $fullPath.Substring(2).TrimStart('\') }
    }
    return $fullPath
}

function Test-SafeReleasePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (-not [string]::IsNullOrWhiteSpace($Path) -and -not [IO.Path]::IsPathRooted($Path) -and -not ($Path -split '[\\/]' -contains '..'))
}

function Get-ServiceHostProcessPath {
    param($Process)
    if ($null -ne $Process.Path) { return [string]$Process.Path }
    if ($null -ne $Process.ExecutablePath) { return [string]$Process.ExecutablePath }
    return ''
}

function Invoke-ServiceHostInstall {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)][string]$PublicDesktopPath,
        [Parameter(Mandatory = $true)][string]$InteractiveUser,
        [Parameter(Mandatory = $true)][string]$InteractiveSid,
        [scriptblock]$GetCurrentIdentityAction = { Get-InteractiveInstallIdentity },
        [scriptblock]$ResolveInteractiveUserSidAction = { param($name) Resolve-InteractiveUserSid -InteractiveUser $name },
        [scriptblock]$TestAdministratorAction = { Test-IsAdministrator },
        [scriptblock]$ReadManifestAction = { param($path) Get-Content -Raw -LiteralPath $path | ConvertFrom-Json },
        [scriptblock]$EnsureDirectoryAction = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null },
        [scriptblock]$CopyFileAction = { param($source, $destination) Copy-Item -LiteralPath $source -Destination $destination -Force },
        [scriptblock]$RemovePathAction = { param($path, [bool]$recurse) Remove-Item -LiteralPath $path -Recurse:$recurse -Force -ErrorAction Stop },
        [scriptblock]$SetEnvironmentVariableAction = { param($name, $value) [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Machine) },
        [scriptblock]$CreateShortcutAction = {
            param($shortcutPath, $targetPath, $workingDirectory)
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $targetPath; $shortcut.WorkingDirectory = $workingDirectory; $shortcut.WindowStyle = 1; $shortcut.Save()
        },
        [scriptblock]$RegisterTaskAction,
        [scriptblock]$StartTaskAction = { param($taskName) Start-ScheduledTask -TaskName $taskName },
        [scriptblock]$GetTaskAction = { param($taskName) Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue },
        [scriptblock]$GetProcessesAction = { Get-CimInstance Win32_Process -Filter "Name='QuickBooksServiceHost.exe'" },
        [scriptblock]$StopProcessAction = { param($process) Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop },
        [scriptblock]$WaitAction = { Start-Sleep -Milliseconds 250 },
        [scriptblock]$ProtectStateTreeAction = { param($path) Protect-ServiceHostStateTree -StatePath $path | Out-Null },
        [scriptblock]$MutexAction = { param($name, $waitMilliseconds, $action) Invoke-WithInstallerMutex -Name $name -WaitMilliseconds $waitMilliseconds -Action $action },
        [ValidateRange(1, 300000)][int]$MutexWaitMilliseconds = 30000,
        [ValidateRange(1, 600)][int]$StopWaitAttempts = 40
    )
    Assert-ElevatedIdentityMatches -InteractiveUser $InteractiveUser -InteractiveSid $InteractiveSid `
        -GetCurrentIdentityAction $GetCurrentIdentityAction -ResolveInteractiveUserSidAction $ResolveInteractiveUserSidAction `
        -TestAdministratorAction $TestAdministratorAction
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) { throw "Source path does not exist: $SourcePath" }
    $manifestPath = Join-Path $SourcePath 'release.manifest.json'
    $manifest = & $ReadManifestAction $manifestPath
    if ([int]$manifest.schema_version -ne 1 -or $null -eq $manifest.manager) { throw 'Unsupported release manifest.' }
    foreach ($entry in @($manifest.files) + @($manifest.manager)) {
        if (-not (Test-SafeReleasePath -Path ([string]$entry.path))) { throw "Unsafe manifest path: $($entry.path)" }
    }

    $managerSource = Join-Path $SourcePath ([string]$manifest.manager.path)
    if (-not (Test-Path -LiteralPath $managerSource -PathType Leaf) -or
        [long](Get-Item -LiteralPath $managerSource).Length -ne [long]$manifest.manager.length -or
        (Get-FileHash -LiteralPath $managerSource -Algorithm SHA256).Hash -ne [string]$manifest.manager.sha256) {
        throw "Manager source verification failed: $($manifest.manager.path)"
    }

    $installState = [pscustomobject]@{
        Plan = $null
        HostPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
        ConnectorPath = ''
        ManagerPath = ''
        ShortcutPath = ''
    }
    $mutation = {
    $hostPath = $installState.HostPath
    $installedProcesses = @(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath })
    foreach ($process in $installedProcesses) { & $StopProcessAction $process }
    if ($installedProcesses.Count -gt 0) {
        $hostExited = $false
        foreach ($attempt in 1..$StopWaitAttempts) {
            $stillRunning = @(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath })
            if ($stillRunning.Count -eq 0) {
                $hostExited = $true
                break
            }
            & $WaitAction
        }
        if (-not $hostExited) { throw "Installed host process did not exit: $hostPath" }
    }

    $managerDirectory = Join-Path $StatePath 'Manager'
    & $ProtectStateTreeAction $StatePath
    if (Test-Path -LiteralPath $InstallPath -PathType Container) { & $RemovePathAction $InstallPath $true }
    & $EnsureDirectoryAction $InstallPath
    & $EnsureDirectoryAction $managerDirectory
    foreach ($entry in @($manifest.files)) {
        $source = Join-Path $SourcePath ([string]$entry.path)
        $target = Join-Path $InstallPath ([string]$entry.path)
        & $EnsureDirectoryAction (Split-Path -Parent $target)
        & $CopyFileAction $source $target
        if ([long](Get-Item -LiteralPath $target).Length -ne [long]$entry.length -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$entry.sha256) { throw "Installed file verification failed: $($entry.path)" }
    }
    $managerTarget = Join-Path $managerDirectory 'service_host_manager.ps1'
    $managerStage = Join-Path $managerDirectory 'service_host_manager.ps1.stage'
    & $CopyFileAction $managerSource $managerStage
    try {
        if ([long](Get-Item -LiteralPath $managerStage).Length -ne [long]$manifest.manager.length -or
            (Get-FileHash -LiteralPath $managerStage -Algorithm SHA256).Hash -ne [string]$manifest.manager.sha256) {
            throw "Staged manager verification failed: $($manifest.manager.path)"
        }
        & $CopyFileAction $managerStage $managerTarget
        if ([long](Get-Item -LiteralPath $managerTarget).Length -ne [long]$manifest.manager.length -or
            (Get-FileHash -LiteralPath $managerTarget -Algorithm SHA256).Hash -ne [string]$manifest.manager.sha256) {
            throw "Installed manager verification failed: $($manifest.manager.path)"
        }
    }
    finally {
        & $RemovePathAction $managerStage $false
    }
    & $CopyFileAction $manifestPath (Join-Path $StatePath 'release.manifest.json')
    & $ProtectStateTreeAction $StatePath

    $connectorPath = Join-Path $InstallPath 'QuickBooksConnectorCli.exe'
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw "Installed executable was not found: $hostPath" }
    if (-not (Test-Path -LiteralPath $connectorPath -PathType Leaf)) { throw "Installed connector CLI was not found: $connectorPath" }
    & $SetEnvironmentVariableAction 'QUOTE_MODULEV2_QB_CONNECTOR_CLI' $connectorPath

    $bridge = @{ QB_BRIDGE_TOKEN = ''; QB_BRIDGE_ORIGIN = 'http://APPSRV01:8742'; QB_BRIDGE_PORT = '8788' }
    $bridgeSettingsPath = Join-Path $SourcePath 'bridge.settings.psd1'
    if (Test-Path -LiteralPath $bridgeSettingsPath -PathType Leaf) {
        $loaded = Import-PowerShellDataFile -LiteralPath $bridgeSettingsPath
        foreach ($key in @($bridge.Keys)) { if ($loaded.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$loaded[$key])) { $bridge[$key] = [string]$loaded[$key] } }
    }
    else {
        Write-Warning 'bridge.settings.psd1 was not found next to the installer; using defaults (blank token).'
    }
    foreach ($key in @('QB_BRIDGE_TOKEN','QB_BRIDGE_ORIGIN','QB_BRIDGE_PORT')) { & $SetEnvironmentVariableAction $key $bridge[$key] }
    if ([string]::IsNullOrWhiteSpace($bridge.QB_BRIDGE_TOKEN)) {
        Write-Warning 'QB_BRIDGE_TOKEN is blank - the QuickBooks bridge will reject all requests with 403 until it is set in bridge.settings.psd1 and the host is restarted.'
    }
    $shortcutPath = Join-Path $PublicDesktopPath 'QuickBooksServiceHost.lnk'
    & $CreateShortcutAction $shortcutPath $hostPath $InstallPath

    $plan = New-ServiceHostTaskPlan -InteractiveUser $InteractiveUser -ManagerPath $managerTarget
    Register-ServiceHostTask -Plan $plan -RegisterTaskAction $RegisterTaskAction
    $installState.Plan = $plan
    $installState.ConnectorPath = $connectorPath
    $installState.ManagerPath = $managerTarget
    $installState.ShortcutPath = $shortcutPath
    }.GetNewClosure()
    & $MutexAction 'Global\QuickBooksServiceHostAutoUpdate' $MutexWaitMilliseconds $mutation | Out-Null

    $plan = $installState.Plan
    $hostPath = $installState.HostPath
    & $StartTaskAction $plan.TaskName
    if ($null -eq (& $GetTaskAction $plan.TaskName)) { throw "Scheduled Task was not found after registration: $($plan.TaskName)" }
    $running = $false
    foreach ($attempt in 1..20) {
        if (@(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath }).Count -gt 0) { $running = $true; break }
        & $WaitAction
    }
    if (-not $running) { throw "Installed host process was not found: $hostPath" }
    return [pscustomobject]@{
        TaskName = $plan.TaskName
        HostPath = $installState.HostPath
        ConnectorPath = $installState.ConnectorPath
        ManagerPath = $installState.ManagerPath
        ShortcutPath = $installState.ShortcutPath
    }
}

if ($AsLibrary) { return }

$identity = Get-InteractiveInstallIdentity
if ([string]::IsNullOrWhiteSpace($InteractiveUser)) { $InteractiveUser = $identity.Name }
if ([string]::IsNullOrWhiteSpace($InteractiveSid)) { $InteractiveSid = $identity.Sid }
$scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
if (-not (Test-IsAdministrator)) {
    $elevatedScriptPath = Resolve-InstallScriptPath -Path $scriptPath
    $arguments = "-NoProfile -ExecutionPolicy Unrestricted -File `"$elevatedScriptPath`" -InteractiveUser `"$InteractiveUser`" -InteractiveSid `"$InteractiveSid`""
    Write-Host 'Administrator permission is required. Relaunching installer...'
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru -ErrorAction Stop
    if ($null -ne $process.ExitCode) { exit $process.ExitCode }
    exit 0
}
Assert-ElevatedIdentityMatches -InteractiveUser $InteractiveUser -InteractiveSid $InteractiveSid
$sourcePath = $PSScriptRoot.Trim()
$destinationPath = (Join-Path $env:ProgramFiles 'QuickBooksServiceHost').Trim()
$statePath = (Join-Path $env:ProgramData 'QuickBooksServiceHost').Trim()
$desktopPath = [Environment]::GetFolderPath('CommonDesktopDirectory')
if ([string]::IsNullOrWhiteSpace($desktopPath)) { $desktopPath = Join-Path $env:Public 'Desktop' }
$result = Invoke-ServiceHostInstall -SourcePath $sourcePath -InstallPath $destinationPath -StatePath $statePath -PublicDesktopPath $desktopPath -InteractiveUser $InteractiveUser -InteractiveSid $InteractiveSid
Write-Output "Destination Path: $destinationPath"
Write-Output "Source Path: $sourcePath"
Write-Output "Target Path: $($result.HostPath)"
Write-Output "Connector CLI Path: $($result.ConnectorPath)"
Write-Output "Manager Path: $($result.ManagerPath)"
Write-Output "Shortcut Path: $($result.ShortcutPath)"
Write-Host 'Installation complete. The scheduled task and public shortcut are ready.'
