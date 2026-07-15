[CmdletBinding()]
param(
    [ValidateSet('manifest-contract', 'host-wins', 'settings-excluded', 'manager-entry', 'copy-corruption', 'manifest-written-last', 'release-id-unborn-fallback', 'manager-source-change', 'all')]
    [string]$Scenario = 'all'
)

$ErrorActionPreference = 'Stop'
$deployScriptPath = Join-Path $PSScriptRoot 'deploy_servicehost.ps1'
$deployScriptContent = Get-Content -Raw -LiteralPath $deployScriptPath
if ($deployScriptContent -notmatch '(?s)\bparam\s*\([\s\S]*\[switch\]\s*\$AsLibrary') {
    throw 'Deploy script does not provide the required -AsLibrary safety boundary.'
}
. $deployScriptPath -AsLibrary

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) {
        throw "$Because. Expected [$Expected], got [$Actual]."
    }
}

function Assert-True([bool]$Condition, [string]$Because) {
    if (-not $Condition) { throw $Because }
}

function Assert-Matches([string]$Pattern, [string]$Actual, [string]$Because) {
    if ($Actual -notmatch $Pattern) {
        throw "$Because. Expected [$Actual] to match [$Pattern]."
    }
}

function Assert-Throws([scriptblock]$Action, [string]$Because) {
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Because
}

function New-DeploymentFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('quoteaddin-deploy-test-' + [guid]::NewGuid().ToString('N'))
    $hostSource = Join-Path $root 'host'
    $cli = Join-Path $root 'cli'
    $dest = Join-Path $root 'destination'
    New-Item -ItemType Directory -Path $hostSource, $cli, $dest -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $hostSource 'nested'), (Join-Path $cli 'nested') -Force | Out-Null

    Set-Content -LiteralPath (Join-Path $cli 'shared.dll') -Value 'cli-loses' -NoNewline
    Set-Content -LiteralPath (Join-Path $hostSource 'shared.dll') -Value 'host-wins' -NoNewline
    Set-Content -LiteralPath (Join-Path $cli 'nested\cli-only.dll') -Value 'cli-only' -NoNewline
    Set-Content -LiteralPath (Join-Path $hostSource 'nested\host-only.dll') -Value 'host-only' -NoNewline
    Set-Content -LiteralPath (Join-Path $cli 'bridge.settings.psd1') -Value 'source-secret' -NoNewline
    Set-Content -LiteralPath (Join-Path $dest 'bridge.settings.psd1') -Value 'preserved-secret' -NoNewline

    $manager = Join-Path $root 'service_host_manager.ps1'
    Set-Content -LiteralPath $manager -Value '# manager' -NoNewline

    [pscustomobject]@{
        Root = $root
        HostSource = $hostSource
        Cli = $cli
        Destination = $dest
        Manager = $manager
    }
}

function Invoke-FixturePublish {
    param($Fixture, [scriptblock]$CopyFileAction)

    $arguments = @{
        HostSourcePath = $Fixture.HostSource
        ConnectorSourcePath = $Fixture.Cli
        ManagerPath = $Fixture.Manager
        DestinationPath = $Fixture.Destination
        ReleaseId = 'abc123'
        PublishedAtUtc = [datetime]'2026-07-15T10:00:00Z'
    }
    if ($null -ne $CopyFileAction) {
        $arguments.CopyFileAction = $CopyFileAction
    }
    Publish-ServiceHostRelease @arguments
}

function Run-Scenario {
    param([Parameter(Mandatory = $true)][string]$Name)

    $fixture = New-DeploymentFixture
    try {
        switch ($Name) {
            'manifest-contract' {
                $manifest = Invoke-FixturePublish -Fixture $fixture
                Assert-Equal 1 $manifest.schema_version 'schema version'
                Assert-Equal 'abc123' $manifest.release_id 'release id'
                Assert-Equal '2026-07-15T10:00:00.0000000Z' $manifest.published_at_utc 'published timestamp'
                Assert-Equal 3 @($manifest.files).Count 'runtime file count'
                foreach ($entry in @($manifest.files)) {
                    $path = Join-Path $fixture.Destination $entry.path
                    Assert-Equal ([long](Get-Item -LiteralPath $path).Length) ([long]$entry.length) 'manifest length'
                    Assert-Equal ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) $entry.sha256 'manifest hash'
                }

                $written = Get-Content -Raw -LiteralPath (Join-Path $fixture.Destination 'release.manifest.json') | ConvertFrom-Json
                Assert-Equal 'abc123' $written.release_id 'written manifest release id'
            }
            'host-wins' {
                $manifest = Invoke-FixturePublish -Fixture $fixture
                Assert-Equal 'host-wins' (Get-Content -Raw -LiteralPath (Join-Path $fixture.Destination 'shared.dll')) 'host copy precedence'
                Assert-True (@($manifest.files.path) -contains 'shared.dll') 'shared dependency must be in the manifest'
            }
            'settings-excluded' {
                $manifest = Invoke-FixturePublish -Fixture $fixture
                Assert-True (-not (@($manifest.files.path) -contains 'bridge.settings.psd1')) 'settings excluded'
                Assert-Equal 'preserved-secret' (Get-Content -Raw -LiteralPath (Join-Path $fixture.Destination 'bridge.settings.psd1')) 'settings preserved'
            }
            'manager-entry' {
                $manifest = Invoke-FixturePublish -Fixture $fixture
                $publishedManager = Join-Path $fixture.Destination 'service_host_manager.ps1'
                Assert-Equal 'service_host_manager.ps1' $manifest.manager.path 'manager path'
                Assert-Equal ([long](Get-Item -LiteralPath $publishedManager).Length) ([long]$manifest.manager.length) 'manager length'
                Assert-Equal ((Get-FileHash -LiteralPath $fixture.Manager -Algorithm SHA256).Hash) $manifest.manager.sha256 'manager hash'
                Assert-Equal $manifest.manager.sha256 ((Get-FileHash -LiteralPath $publishedManager -Algorithm SHA256).Hash) 'published manager verification'
            }
            'copy-corruption' {
                $manifestPath = Join-Path $fixture.Destination 'release.manifest.json'
                [IO.File]::WriteAllBytes($manifestPath, [Text.Encoding]::UTF8.GetBytes('pre-existing manifest bytes'))
                $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath))
                $corruptCopy = {
                    param([string]$Source, [string]$Destination)
                    Copy-Item -LiteralPath $Source -Destination $Destination -Force
                    Set-Content -LiteralPath $Destination -Value 'corrupt' -NoNewline
                }

                Assert-Throws { Invoke-FixturePublish -Fixture $fixture -CopyFileAction $corruptCopy } 'copy corruption must abort publication'
                $after = [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath))
                Assert-Equal $before $after 'failed publication must leave final manifest byte-for-byte unchanged'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Destination 'release.manifest.json.pending'))) 'failed publication must not leave a pending manifest'
            }
            'manifest-written-last' {
                $oldTimestamp = [datetime]'2000-01-01T00:00:00Z'
                Get-ChildItem -LiteralPath $fixture.HostSource, $fixture.Cli -Recurse -File | ForEach-Object { $_.LastWriteTimeUtc = $oldTimestamp }
                (Get-Item -LiteralPath $fixture.Manager).LastWriteTimeUtc = $oldTimestamp

                $manifest = Invoke-FixturePublish -Fixture $fixture
                $manifestPath = Join-Path $fixture.Destination 'release.manifest.json'
                Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'final manifest must exist'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Destination 'release.manifest.json.pending'))) 'pending manifest must be renamed away'
                $completionTime = (Get-Item -LiteralPath $manifestPath).LastWriteTimeUtc
                foreach ($relativePath in @($manifest.files.path) + @($manifest.manager.path)) {
                    $payloadTime = (Get-Item -LiteralPath (Join-Path $fixture.Destination $relativePath)).LastWriteTimeUtc
                    Assert-True ($completionTime -ge $payloadTime) 'final manifest must be the last published runtime artifact'
                }
            }
            'release-id-unborn-fallback' {
                $repositoryPath = Join-Path $fixture.Root 'unborn-repository'
                & git init --quiet $repositoryPath
                if ($LASTEXITCODE) { throw 'Failed to initialize temporary unborn repository.' }

                # Reproduce Git variants that print symbolic HEAD while still returning 128.
                $gitShimPath = Join-Path $fixture.Root 'git.cmd'
                [IO.File]::WriteAllText($gitShimPath, "@echo HEAD`r`n@exit /b 128`r`n", [Text.Encoding]::ASCII)
                $previousPath = $env:PATH
                try {
                    $env:PATH = $fixture.Root + ';' + $previousPath
                    $releaseId = Get-DefaultReleaseId -RepositoryPath $repositoryPath
                }
                finally {
                    $env:PATH = $previousPath
                }
                Assert-Matches '^\d{8}T\d{13}Z$' $releaseId 'unborn repository must use the UTC timestamp fallback'
                Assert-True ($releaseId -ne 'HEAD') 'failed git output must never become the release ID'
            }
            'manager-source-change' {
                $managerSourcePath = $fixture.Manager
                $originalManifestFunction = (Get-Command New-ReleaseManifest -CommandType Function).ScriptBlock
                try {
                    Set-Item -LiteralPath Function:\New-ReleaseManifest -Value {
                        param(
                            [string]$PayloadRoot,
                            [string]$ManagerPath,
                            [string]$ReleaseId,
                            [datetime]$PublishedAtUtc
                        )
                        Set-Content -LiteralPath $managerSourcePath -Value '# changed after verified copy' -NoNewline
                        & $originalManifestFunction -PayloadRoot $PayloadRoot -ManagerPath $ManagerPath `
                            -ReleaseId $ReleaseId -PublishedAtUtc $PublishedAtUtc
                    }

                    $manifest = Invoke-FixturePublish -Fixture $fixture
                }
                finally {
                    Set-Item -LiteralPath Function:\New-ReleaseManifest -Value $originalManifestFunction
                }

                $publishedManager = Join-Path $fixture.Destination 'service_host_manager.ps1'
                $publishedHash = (Get-FileHash -LiteralPath $publishedManager -Algorithm SHA256).Hash
                $changedSourceHash = (Get-FileHash -LiteralPath $fixture.Manager -Algorithm SHA256).Hash
                Assert-True ($changedSourceHash -ne $publishedHash) 'test must mutate the manager source after its destination copy'
                Assert-Equal $publishedHash $manifest.manager.sha256 'manager manifest entry must describe the verified destination bytes'
            }
            default {
                throw "Unknown scenario: $Name"
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$allScenarios = @(
    'manifest-contract',
    'host-wins',
    'settings-excluded',
    'manager-entry',
    'copy-corruption',
    'manifest-written-last',
    'release-id-unborn-fallback',
    'manager-source-change'
)

if ($Scenario -eq 'all') {
    foreach ($scenarioName in $allScenarios) {
        Run-Scenario -Name $scenarioName
    }
}
else {
    Run-Scenario -Name $Scenario
}
