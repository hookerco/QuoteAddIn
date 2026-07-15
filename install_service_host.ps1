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

function Invoke-ServiceHostNativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [scriptblock]$InvokeNativeAction = {
            param($nativeFilePath, $nativeArguments)
            $nativeOutput = @(& $nativeFilePath @nativeArguments 2>&1)
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($nativeOutput -join [Environment]::NewLine) }
        }
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $records = @(& $InvokeNativeAction $FilePath $Arguments 2>&1)
        $result = @($records | Where-Object {
            $null -ne $_ -and $null -ne $_.PSObject.Properties['ExitCode']
        } | Select-Object -Last 1)
        if ($result.Count -ne 1) {
            throw "Native command did not return an exit code: $FilePath"
        }

        $diagnostics = New-Object Collections.Generic.List[string]
        foreach ($record in $records) {
            if ([object]::ReferenceEquals($record, $result[0])) { continue }
            $text = if ($record -is [Management.Automation.ErrorRecord]) { $record.Exception.Message } else { [string]$record }
            if (-not [string]::IsNullOrWhiteSpace($text)) { $diagnostics.Add($text) }
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result[0].Output)) { $diagnostics.Add([string]$result[0].Output) }
        $output = (($diagnostics -join [Environment]::NewLine) -replace '[\x00-\x08\x0B\x0C\x0E-\x1F]', '').Trim()
        if ($output.Length -gt 2048) { $output = $output.Substring(0, 2048) }
        return [pscustomobject]@{ ExitCode = [int]$result[0].ExitCode; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Set-ServiceHostPathOwner {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$OwnerSid,
        [scriptblock]$InvokeTakeOwnershipAction = { param($filePath, $arguments) Invoke-ServiceHostNativeCommand -FilePath $filePath -Arguments $arguments }
    )

    if ($OwnerSid.Value -ne 'S-1-5-32-544') { throw 'Service host state owner must be BUILTIN\Administrators.' }
    $takeownPath = if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) { 'takeown.exe' } else { Join-Path $env:SystemRoot 'System32\takeown.exe' }
    $arguments = @('/F', $Path, '/A')
    $result = & $InvokeTakeOwnershipAction $takeownPath $arguments
    if ($null -eq $result -or [int]$result.ExitCode -ne 0) {
        $exitCode = if ($null -eq $result) { -1 } else { [int]$result.ExitCode }
        throw "Failed to take Administrators ownership of service host state path: $Path (exit $exitCode)."
    }
}

function Protect-ServiceHostStateTree {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [scriptblock]$EnsureDirectoryAction = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null },
        [scriptblock]$GetItemAction = { param($path) Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue },
        [scriptblock]$GetChildrenAction = { param($path) @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) },
        [scriptblock]$GetAclAction = { param($path) Get-Acl -LiteralPath $path },
        [scriptblock]$SetOwnerAction = { param($path, $ownerSid) Set-ServiceHostPathOwner -Path $path -OwnerSid $ownerSid },
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

function New-ServiceHostRuntimeAcl {
    param([Parameter(Mandatory = $true)][bool]$IsContainer)

    $acl = if ($IsContainer) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else {
        New-Object Security.AccessControl.FileSecurity
    }
    $administrators = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $system = New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
    $users = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = if ($IsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($entry in @(
        [pscustomobject]@{ Sid = $system; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Sid = $administrators; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Sid = $users; Rights = [Security.AccessControl.FileSystemRights]::ReadAndExecute }
    )) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $entry.Sid,
            $entry.Rights,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Test-ServiceHostRuntimeAcl {
    param(
        [Parameter(Mandatory = $true)]$Acl,
        [Parameter(Mandatory = $true)][bool]$IsContainer
    )

    try { $owner = $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value }
    catch { return $false }
    if ($owner -ne 'S-1-5-32-544' -or -not $Acl.AreAccessRulesProtected) { return $false }
    $expectedInheritance = if ($IsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    $expected = @{
        'S-1-5-18' = [Security.AccessControl.FileSystemRights]::FullControl
        'S-1-5-32-544' = [Security.AccessControl.FileSystemRights]::FullControl
        'S-1-5-32-545' = ([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize)
    }
    $rules = @($Acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $expected.Count) { return $false }
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expected.ContainsKey($sid) -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne $expected[$sid] -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            $rule.IsInherited) {
            return $false
        }
    }
    return $true
}

function Protect-ServiceHostRuntimeTree {
    param(
        [Parameter(Mandatory = $true)][string]$InstallPath,
        [scriptblock]$GetItemAction = { param($path) Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue },
        [scriptblock]$GetChildrenAction = { param($path) @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) },
        [scriptblock]$SetAclAction = { param($path, $acl) Set-Acl -LiteralPath $path -AclObject $acl },
        [scriptblock]$GetAclAction = { param($path) Get-Acl -LiteralPath $path }
    )

    $rootItem = & $GetItemAction $InstallPath
    if ($null -eq $rootItem -or -not [bool]$rootItem.PSIsContainer) { throw "Install path is not a directory: $InstallPath" }
    $queue = New-Object 'Collections.Generic.Queue[object]'
    $queue.Enqueue($rootItem)
    $visited = @{}
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $freshItem = & $GetItemAction $item.FullName
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $key = ([string]$freshItem.FullName).ToLowerInvariant()
        if ($visited.ContainsKey($key)) { continue }
        $visited[$key] = $true
        $runtimeAcl = New-ServiceHostRuntimeAcl -IsContainer ([bool]$freshItem.PSIsContainer)
        & $SetAclAction $freshItem.FullName $runtimeAcl | Out-Null
        $freshItem = & $GetItemAction $freshItem.FullName
        Assert-ServiceHostStateItemSafe -Item $freshItem
        $verifiedAcl = & $GetAclAction $freshItem.FullName
        if (-not (Test-ServiceHostRuntimeAcl -Acl $verifiedAcl -IsContainer ([bool]$freshItem.PSIsContainer))) {
            throw "Service host runtime ACL verification failed: $($freshItem.FullName)"
        }
        if ([bool]$freshItem.PSIsContainer) {
            foreach ($child in @(& $GetChildrenAction $freshItem.FullName)) {
                Assert-ServiceHostStateItemSafe -Item $child
                $queue.Enqueue($child)
            }
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

function Get-ServiceHostVolumeIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $candidate)) {
        $parent = Split-Path -Parent $candidate
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $candidate) {
            throw "Unable to resolve an existing ancestor for volume identity: $Path"
        }
        $candidate = $parent
    }
    $volume = Get-Volume -FilePath $candidate -ErrorAction Stop
    $identity = if (-not [string]::IsNullOrWhiteSpace([string]$volume.UniqueId)) {
        [string]$volume.UniqueId
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$volume.ObjectId)) {
        [string]$volume.ObjectId
    }
    else { '' }
    if ([string]::IsNullOrWhiteSpace($identity)) { throw "Unable to resolve volume identity: $Path" }
    return $identity
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
        [scriptblock]$MovePathAction = { param($source, $destination) Move-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop },
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
        [scriptblock]$ProtectRuntimeTreeAction = { param($path) Protect-ServiceHostRuntimeTree -InstallPath $path | Out-Null },
        [scriptblock]$GetVolumeIdentityAction = { param($path) Get-ServiceHostVolumeIdentity -Path $path },
        [scriptblock]$MutexAction = { param($name, $waitMilliseconds, $action) Invoke-WithInstallerMutex -Name $name -WaitMilliseconds $waitMilliseconds -Action $action },
        [scriptblock]$NeutralizeTaskAction = {
            param($taskName)
            if ($null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
            }
        },
        [ValidateRange(1, 300000)][int]$MutexWaitMilliseconds = 30000,
        [ValidateRange(1, 600)][int]$StopWaitAttempts = 40
    )

    Assert-ElevatedIdentityMatches -InteractiveUser $InteractiveUser -InteractiveSid $InteractiveSid `
        -GetCurrentIdentityAction $GetCurrentIdentityAction -ResolveInteractiveUserSidAction $ResolveInteractiveUserSidAction `
        -TestAdministratorAction $TestAdministratorAction

    $installVolume = [string](& $GetVolumeIdentityAction $InstallPath)
    $stateVolume = [string](& $GetVolumeIdentityAction $StatePath)
    if ([string]::IsNullOrWhiteSpace($installVolume) -or [string]::IsNullOrWhiteSpace($stateVolume) -or
        -not $installVolume.Equals($stateVolume, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'InstallPath and StatePath must be on the same volume for transactional directory moves.'
    }

    $taskName = 'QuickBooksServiceHost Auto Start and Update'
    $hostPath = Join-Path $InstallPath 'QuickBooksServiceHost.exe'
    $connectorPath = Join-Path $InstallPath 'QuickBooksConnectorCli.exe'
    $managerDirectory = Join-Path $StatePath 'Manager'
    $managerTarget = Join-Path $managerDirectory 'service_host_manager.ps1'
    $installedManifestTarget = Join-Path $StatePath 'release.manifest.json'
    $previousPath = Join-Path $StatePath 'Previous'
    $previousPayloadPath = Join-Path $previousPath 'Payload'
    $previousManagerDirectory = Join-Path $previousPath 'Manager'
    $previousManagerPath = Join-Path $previousManagerDirectory 'service_host_manager.ps1'
    $previousManifestPath = Join-Path $previousPath 'release.manifest.json'
    $plan = New-ServiceHostTaskPlan -InteractiveUser $InteractiveUser -ManagerPath $managerTarget

    $installState = [pscustomobject]@{
        Phase = 'Initialized'
        StageRoot = ''
        StagePayloadPath = ''
        StageManagerPath = ''
        StageManifestPath = ''
        StagePreviousPath = ''
        DisplacedPreviousPath = ''
        Manifest = $null
        Bridge = $null
        HadCurrentInstall = $false
        HadPreviousRuntime = $false
        HadPreviousManager = $false
        HadPreviousManifest = $false
        TaskNeutralized = $false
        StopAttempted = $false
        Stopped = $false
        CurrentMoved = $false
        PromotionHappened = $false
        ManagerPromoted = $false
        ManifestPromoted = $false
        PreviousDisplaced = $false
        PreviousPrepared = $false
        RollbackPrepared = $false
        Plan = $plan
    }

    $stopExpectedHost = {
        $installedProcesses = @(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath })
        foreach ($process in $installedProcesses) { & $StopProcessAction $process }
        if ($installedProcesses.Count -eq 0) { return }
        foreach ($attempt in 1..$StopWaitAttempts) {
            $stillRunning = @(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath })
            if ($stillRunning.Count -eq 0) { return }
            & $WaitAction
        }
        throw "Installed host process did not exit: $hostPath"
    }.GetNewClosure()

    $startAndVerify = {
        & $StartTaskAction $plan.TaskName
        if ($null -eq (& $GetTaskAction $plan.TaskName)) {
            throw "Scheduled Task was not found after registration: $($plan.TaskName)"
        }
        foreach ($attempt in 1..20) {
            $expected = @(& $GetProcessesAction | Where-Object { (Get-ServiceHostProcessPath $_) -ieq $hostPath })
            if ($expected.Count -eq 1) { return }
            if ($expected.Count -gt 1) { throw "Multiple installed host processes were found: $hostPath" }
            & $WaitAction
        }
        throw "Installed host process was not found: $hostPath"
    }.GetNewClosure()

    $invokeMoveAndInspect = {
        param([string]$source, [string]$destination, [string]$flagName = '')
        try { & $MovePathAction $source $destination }
        finally {
            if (-not [string]::IsNullOrWhiteSpace($flagName)) {
                $installState.$flagName = (-not (Test-Path -LiteralPath $source)) -and (Test-Path -LiteralPath $destination)
            }
        }
    }.GetNewClosure()

    $cleanupStageLocked = {
        if (-not [string]::IsNullOrWhiteSpace($installState.StageRoot) -and (Test-Path -LiteralPath $installState.StageRoot)) {
            & $ProtectStateTreeAction $StatePath
            & $RemovePathAction $installState.StageRoot $true
        }
    }.GetNewClosure()

    $restorePreflightPreviousLocked = {
        if ($installState.PreviousPrepared -and (Test-Path -LiteralPath $previousPath)) {
            & $ProtectStateTreeAction $StatePath
            & $RemovePathAction $previousPath $true
        }
        if ($installState.PreviousDisplaced -and (Test-Path -LiteralPath $installState.DisplacedPreviousPath -PathType Container)) {
            & $ProtectStateTreeAction $StatePath
            & $invokeMoveAndInspect $installState.DisplacedPreviousPath $previousPath ''
        }
    }.GetNewClosure()

    $rollbackLocked = {
        $installState.Phase = 'RollingBack'
        $rollbackFailure = $null
        try {
            & $NeutralizeTaskAction $taskName
            & $stopExpectedHost

            if ($installState.PromotionHappened -and (Test-Path -LiteralPath $InstallPath)) {
                & $RemovePathAction $InstallPath $true
            }
            if ($installState.CurrentMoved -and (Test-Path -LiteralPath $previousPayloadPath -PathType Container)) {
                & $ProtectStateTreeAction $StatePath
                & $invokeMoveAndInspect $previousPayloadPath $InstallPath ''
                & $ProtectRuntimeTreeAction $InstallPath
            }

            if ($installState.HadPreviousManager -and (Test-Path -LiteralPath $previousManagerPath -PathType Leaf)) {
                & $ProtectStateTreeAction $StatePath
                & $invokeMoveAndInspect $previousManagerPath $managerTarget ''
            }
            elseif ($installState.ManagerPromoted -and (Test-Path -LiteralPath $managerTarget)) {
                & $ProtectStateTreeAction $StatePath
                & $RemovePathAction $managerTarget $false
            }

            if ($installState.HadPreviousManifest -and (Test-Path -LiteralPath $previousManifestPath -PathType Leaf)) {
                & $ProtectStateTreeAction $StatePath
                & $invokeMoveAndInspect $previousManifestPath $installedManifestTarget ''
            }
            elseif ($installState.ManifestPromoted -and (Test-Path -LiteralPath $installedManifestTarget)) {
                & $ProtectStateTreeAction $StatePath
                & $RemovePathAction $installedManifestTarget $false
            }

            if (Test-Path -LiteralPath $previousPath) {
                & $ProtectStateTreeAction $StatePath
                & $RemovePathAction $previousPath $true
            }
            if ($installState.HadPreviousRuntime) {
                & $ProtectStateTreeAction $StatePath
                Register-ServiceHostTask -Plan $plan -RegisterTaskAction $RegisterTaskAction
                $installState.RollbackPrepared = $true
            }
            $installState.Phase = 'RollbackRestored'
        }
        catch { $rollbackFailure = $_ }
        $cleanupFailure = $null
        try { & $cleanupStageLocked }
        catch { $cleanupFailure = $_ }
        if ($null -ne $cleanupFailure) {
            if ($null -ne $rollbackFailure) { $rollbackFailure.Exception.Data['StageCleanupFailure'] = $cleanupFailure.Exception.Message }
            else { $cleanupFailure.Exception.Data['StageCleanupFailure'] = $cleanupFailure.Exception.Message }
        }
        if ($null -ne $rollbackFailure) { throw $rollbackFailure }
        if ($null -ne $cleanupFailure) { throw $cleanupFailure }
    }.GetNewClosure()

    $mutation = {
        try {
            if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
                throw "Source path does not exist: $SourcePath"
            }
            $manifestPath = Join-Path $SourcePath 'release.manifest.json'
            $manifestLengthBeforeRead = [long](Get-Item -LiteralPath $manifestPath -ErrorAction Stop).Length
            $manifestHashBeforeRead = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256 -ErrorAction Stop).Hash
            $manifest = & $ReadManifestAction $manifestPath
            $manifestSourceLength = [long](Get-Item -LiteralPath $manifestPath -ErrorAction Stop).Length
            $manifestSourceHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256 -ErrorAction Stop).Hash
            if ($manifestLengthBeforeRead -ne $manifestSourceLength -or $manifestHashBeforeRead -ne $manifestSourceHash) {
                throw 'Release manifest changed during validation: release.manifest.json'
            }
            if ([int]$manifest.schema_version -ne 1 -or $null -eq $manifest.manager) {
                throw 'Unsupported release manifest.'
            }
            foreach ($entry in @($manifest.files) + @($manifest.manager)) {
                if (-not (Test-SafeReleasePath -Path ([string]$entry.path))) {
                    throw "Unsafe manifest path: $($entry.path)"
                }
            }
            $installState.Manifest = $manifest

            $installState.HadCurrentInstall = Test-Path -LiteralPath $InstallPath -PathType Container
            $installState.HadPreviousRuntime = Test-Path -LiteralPath $hostPath -PathType Leaf
            $installState.HadPreviousManager = Test-Path -LiteralPath $managerTarget -PathType Leaf
            $installState.HadPreviousManifest = Test-Path -LiteralPath $installedManifestTarget -PathType Leaf
            if ($installState.HadPreviousRuntime -and -not $installState.HadPreviousManager) {
                throw 'Existing runtime requires a verified manager snapshot: service_host_manager.ps1'
            }
            if ($installState.HadPreviousRuntime -and -not $installState.HadPreviousManifest) {
                throw 'Existing runtime requires a verified release manifest snapshot: release.manifest.json'
            }

            $stageRoot = Join-Path $StatePath ([guid]::NewGuid().ToString('N'))
            $stagePayloadPath = Join-Path $stageRoot 'Payload'
            $stageManagerDirectory = Join-Path $stageRoot 'Manager'
            $stageManagerPath = Join-Path $stageManagerDirectory 'service_host_manager.ps1'
            $stageManifestPath = Join-Path $stageRoot 'release.manifest.json'
            $stagePreviousPath = Join-Path $stageRoot 'PreparedPrevious'
            $stagePreviousManagerDirectory = Join-Path $stagePreviousPath 'Manager'
            $stagePreviousManagerPath = Join-Path $stagePreviousManagerDirectory 'service_host_manager.ps1'
            $stagePreviousManifestPath = Join-Path $stagePreviousPath 'release.manifest.json'
            $displacedPreviousPath = Join-Path $stageRoot 'DisplacedPrevious'
            $installState.StageRoot = $stageRoot
            $installState.StagePayloadPath = $stagePayloadPath
            $installState.StageManagerPath = $stageManagerPath
            $installState.StageManifestPath = $stageManifestPath
            $installState.StagePreviousPath = $stagePreviousPath
            $installState.DisplacedPreviousPath = $displacedPreviousPath

            foreach ($directory in @($stageRoot, $stagePayloadPath, $stageManagerDirectory, $stagePreviousPath, $stagePreviousManagerDirectory)) {
                & $ProtectStateTreeAction $StatePath
                & $EnsureDirectoryAction $directory
            }
            if ($installState.HadPreviousManager) {
                $previousManagerLength = [long](Get-Item -LiteralPath $managerTarget -ErrorAction Stop).Length
                $previousManagerHash = (Get-FileHash -LiteralPath $managerTarget -Algorithm SHA256 -ErrorAction Stop).Hash
                & $ProtectStateTreeAction $StatePath
                & $CopyFileAction $managerTarget $stagePreviousManagerPath
                if ([long](Get-Item -LiteralPath $stagePreviousManagerPath -ErrorAction Stop).Length -ne $previousManagerLength -or
                    (Get-FileHash -LiteralPath $stagePreviousManagerPath -Algorithm SHA256 -ErrorAction Stop).Hash -ne $previousManagerHash) {
                    throw 'Previous manager snapshot verification failed: service_host_manager.ps1'
                }
            }
            if ($installState.HadPreviousManifest) {
                $previousManifestLength = [long](Get-Item -LiteralPath $installedManifestTarget -ErrorAction Stop).Length
                $previousManifestHash = (Get-FileHash -LiteralPath $installedManifestTarget -Algorithm SHA256 -ErrorAction Stop).Hash
                & $ProtectStateTreeAction $StatePath
                & $CopyFileAction $installedManifestTarget $stagePreviousManifestPath
                if ([long](Get-Item -LiteralPath $stagePreviousManifestPath -ErrorAction Stop).Length -ne $previousManifestLength -or
                    (Get-FileHash -LiteralPath $stagePreviousManifestPath -Algorithm SHA256 -ErrorAction Stop).Hash -ne $previousManifestHash) {
                    throw 'Previous release manifest snapshot verification failed: release.manifest.json'
                }
            }
            $installState.Phase = 'PreviousSnapshotted'
            foreach ($entry in @($manifest.files)) {
                $source = Join-Path $SourcePath ([string]$entry.path)
                $target = Join-Path $stagePayloadPath ([string]$entry.path)
                & $ProtectStateTreeAction $StatePath
                & $EnsureDirectoryAction (Split-Path -Parent $target)
                & $ProtectStateTreeAction $StatePath
                & $CopyFileAction $source $target
                if ([long](Get-Item -LiteralPath $target -ErrorAction Stop).Length -ne [long]$entry.length -or
                    (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash -ne [string]$entry.sha256) {
                    throw "Staged file verification failed: $($entry.path)"
                }
            }

            $managerSource = Join-Path $SourcePath ([string]$manifest.manager.path)
            & $ProtectStateTreeAction $StatePath
            & $CopyFileAction $managerSource $stageManagerPath
            if ([long](Get-Item -LiteralPath $stageManagerPath -ErrorAction Stop).Length -ne [long]$manifest.manager.length -or
                (Get-FileHash -LiteralPath $stageManagerPath -Algorithm SHA256 -ErrorAction Stop).Hash -ne [string]$manifest.manager.sha256) {
                throw "Staged manager verification failed: $($manifest.manager.path)"
            }

            & $ProtectStateTreeAction $StatePath
            & $CopyFileAction $manifestPath $stageManifestPath
            if ([long](Get-Item -LiteralPath $stageManifestPath -ErrorAction Stop).Length -ne $manifestSourceLength -or
                (Get-FileHash -LiteralPath $stageManifestPath -Algorithm SHA256 -ErrorAction Stop).Hash -ne $manifestSourceHash) {
                throw 'Staged release manifest verification failed: release.manifest.json'
            }

            $bridge = @{ QB_BRIDGE_TOKEN = ''; QB_BRIDGE_ORIGIN = 'http://APPSRV01:8742'; QB_BRIDGE_PORT = '8788' }
            $bridgeSettingsPath = Join-Path $SourcePath 'bridge.settings.psd1'
            if (Test-Path -LiteralPath $bridgeSettingsPath -PathType Leaf) {
                $loaded = Import-PowerShellDataFile -LiteralPath $bridgeSettingsPath
                foreach ($key in @($bridge.Keys)) {
                    if ($loaded.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$loaded[$key])) {
                        $bridge[$key] = [string]$loaded[$key]
                    }
                }
            }
            else {
                Write-Warning 'bridge.settings.psd1 was not found next to the installer; using defaults (blank token).'
            }
            $installState.Bridge = $bridge
            $installState.Phase = 'StageVerified'

            if (Test-Path -LiteralPath $previousPath) {
                & $ProtectStateTreeAction $StatePath
                & $invokeMoveAndInspect $previousPath $displacedPreviousPath 'PreviousDisplaced'
            }
            & $ProtectStateTreeAction $StatePath
            & $invokeMoveAndInspect $stagePreviousPath $previousPath 'PreviousPrepared'
            $installState.Phase = 'PreviousPrepared'

            try { & $NeutralizeTaskAction $taskName }
            finally { $installState.TaskNeutralized = $null -eq (& $GetTaskAction $taskName) }
            $installState.Phase = 'TaskNeutralized'
            $installState.StopAttempted = $true
            & $stopExpectedHost
            $installState.Stopped = $true
            $installState.Phase = 'Stopped'

            if ($installState.HadCurrentInstall) {
                & $ProtectStateTreeAction $StatePath
                & $invokeMoveAndInspect $InstallPath $previousPayloadPath 'CurrentMoved'
            }
            $installState.Phase = 'CurrentMoved'

            & $ProtectStateTreeAction $StatePath
            & $invokeMoveAndInspect $stagePayloadPath $InstallPath 'PromotionHappened'
            & $ProtectRuntimeTreeAction $InstallPath
            $installState.Phase = 'PayloadPromoted'

            & $ProtectStateTreeAction $StatePath
            & $invokeMoveAndInspect $stageManagerPath $managerTarget 'ManagerPromoted'
            $installState.Phase = 'ManagerPromoted'
            if ([long](Get-Item -LiteralPath $managerTarget -ErrorAction Stop).Length -ne [long]$manifest.manager.length -or
                (Get-FileHash -LiteralPath $managerTarget -Algorithm SHA256 -ErrorAction Stop).Hash -ne [string]$manifest.manager.sha256) {
                throw "Promoted manager verification failed: $($manifest.manager.path)"
            }

            & $ProtectStateTreeAction $StatePath
            & $invokeMoveAndInspect $stageManifestPath $installedManifestTarget 'ManifestPromoted'
            $installState.Phase = 'ManifestPromoted'
            if ([long](Get-Item -LiteralPath $installedManifestTarget -ErrorAction Stop).Length -ne $manifestSourceLength -or
                (Get-FileHash -LiteralPath $installedManifestTarget -Algorithm SHA256 -ErrorAction Stop).Hash -ne $manifestSourceHash) {
                throw 'Promoted release manifest verification failed: release.manifest.json'
            }

            & $cleanupStageLocked

            if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
                throw "Installed executable was not found: $hostPath"
            }
            if (-not (Test-Path -LiteralPath $connectorPath -PathType Leaf)) {
                throw "Installed connector CLI was not found: $connectorPath"
            }
            & $SetEnvironmentVariableAction 'QUOTE_MODULEV2_QB_CONNECTOR_CLI' $connectorPath
            foreach ($key in @('QB_BRIDGE_TOKEN','QB_BRIDGE_ORIGIN','QB_BRIDGE_PORT')) {
                & $SetEnvironmentVariableAction $key $bridge[$key]
            }
            if ([string]::IsNullOrWhiteSpace($bridge.QB_BRIDGE_TOKEN)) {
                Write-Warning 'QB_BRIDGE_TOKEN is blank - the QuickBooks bridge will reject all requests with 403 until it is set in bridge.settings.psd1 and the host is restarted.'
            }
            $shortcutPath = Join-Path $PublicDesktopPath 'QuickBooksServiceHost.lnk'
            & $CreateShortcutAction $shortcutPath $hostPath $InstallPath

            & $ProtectStateTreeAction $StatePath
            Register-ServiceHostTask -Plan $plan -RegisterTaskAction $RegisterTaskAction
            $installState.Phase = 'Registered'
        }
        catch {
            $primaryFailure = $_
            if ($installState.Stopped) {
                try { & $rollbackLocked }
                catch {
                    $primaryFailure.Exception.Data['RollbackFailure'] = $_.Exception.Message
                    $primaryFailure.Exception.Data['TaskNeutralizationFailure'] = $_.Exception.Message
                    if ($null -ne $_.Exception.Data['StageCleanupFailure']) {
                        $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Data['StageCleanupFailure']
                    }
                }
            }
            elseif ($installState.TaskNeutralized) {
                $recoveryFailure = $null
                try {
                    & $restorePreflightPreviousLocked
                }
                catch { $recoveryFailure = $_ }
                if ($installState.HadPreviousRuntime) {
                    try {
                        & $ProtectStateTreeAction $StatePath
                        Register-ServiceHostTask -Plan $plan -RegisterTaskAction $RegisterTaskAction
                        if ($installState.StopAttempted) { $installState.RollbackPrepared = $true }
                    }
                    catch { if ($null -eq $recoveryFailure) { $recoveryFailure = $_ } }
                }
                try { & $cleanupStageLocked }
                catch { $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Message }
                if ($null -ne $recoveryFailure) { $primaryFailure.Exception.Data['RollbackFailure'] = $recoveryFailure.Exception.Message }
            }
            else {
                try {
                    & $restorePreflightPreviousLocked
                    & $cleanupStageLocked
                }
                catch { $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Message }
            }
            throw $primaryFailure
        }
    }.GetNewClosure()

    try {
        & $MutexAction 'Global\QuickBooksServiceHostAutoUpdate' $MutexWaitMilliseconds $mutation | Out-Null
    }
    catch {
        $primaryFailure = $_
        if ($installState.RollbackPrepared) {
            try { & $startAndVerify }
            catch { $primaryFailure.Exception.Data['RollbackFailure'] = $_.Exception.Message }
        }
        throw $primaryFailure
    }

    try {
        & $startAndVerify
        $installState.Phase = 'Verified'
    }
    catch {
        $primaryFailure = $_
        try {
            $lateRollback = { & $rollbackLocked }.GetNewClosure()
            & $MutexAction 'Global\QuickBooksServiceHostAutoUpdate' $MutexWaitMilliseconds $lateRollback | Out-Null
        }
        catch {
            $primaryFailure.Exception.Data['RollbackFailure'] = $_.Exception.Message
            $primaryFailure.Exception.Data['TaskNeutralizationFailure'] = $_.Exception.Message
            if ($null -ne $_.Exception.Data['StageCleanupFailure']) {
                $primaryFailure.Exception.Data['StageCleanupFailure'] = $_.Exception.Data['StageCleanupFailure']
            }
        }
        if ($installState.RollbackPrepared) {
            try { & $startAndVerify }
            catch { $primaryFailure.Exception.Data['RollbackFailure'] = $_.Exception.Message }
        }
        throw $primaryFailure
    }

    return [pscustomobject]@{
        TaskName = $plan.TaskName
        HostPath = $hostPath
        ConnectorPath = $connectorPath
        ManagerPath = $managerTarget
        ShortcutPath = Join-Path $PublicDesktopPath 'QuickBooksServiceHost.lnk'
        Phase = $installState.Phase
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
