<# Verifies real imports against disposable filesystem fixtures; does not run or create a production Unity project. #>
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureDirectory = Join-Path $repoDirectory ('TestResults/unity-import-' + [Guid]::NewGuid().ToString('N'))
$installer = Join-Path $repoDirectory 'tools/InstallUnityClient.ps1'
foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $fixtureDirectory $directory) -Force | Out-Null
}
[IO.File]::WriteAllText((Join-Path $fixtureDirectory 'ProjectSettings/ProjectVersion.txt'), 'm_EditorVersion: 6000.6.3f1')
[IO.File]::WriteAllText((Join-Path $fixtureDirectory 'Packages/manifest.json'), '{"dependencies":{"com.unity.render-pipelines.universal":"17.0.0"}}')
& $installer -ProjectPath $fixtureDirectory -DotNetPath $DotNetPath
$recordPath = Join-Path $fixtureDirectory 'OmniTwinClient.import.json'
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$expectedPaths = @('Assets/_OmniTwin/Plugins/OmniTwin.Contracts.dll', 'Assets/_OmniTwin/Plugins/OmniTwin.Transport.dll',
    'Assets/_OmniTwin/Plugins/System.Text.Json.dll', 'Assets/_OmniTwin/Scripts/Network/TwinWebSocketClient.cs',
    'Assets/_OmniTwin/Scripts/World/DynamicObstacleSpawner.cs', 'Assets/_OmniTwin/Plugins/OmniTwin.Policies.dll',
    'Assets/_OmniTwin/Scripts/Agents/SwarmAgentController.cs', 'Assets/_OmniTwin/Scripts/Runtime/PolicyExecutor.cs',
    'Assets/_OmniTwin/Plugins/OmniTwin.Perception.dll', 'Assets/_OmniTwin/Scripts/Media/MediaConsoleController.cs',
    'Assets/_OmniTwin/Scripts/Sensors/LidarSensor.cs', 'Assets/_OmniTwin/Scripts/Media/VisualServicesController.cs')
foreach ($path in $expectedPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $fixtureDirectory $path))) { throw "Missing real imported artifact: $path" }
}
if ($record.targetFramework -ne 'netstandard2.1') { throw 'Wrong imported framework.' }
if (@($record.files).Count -ne 16) { throw 'Expected nine DLLs and seven canonical client scripts.' }
$clientPath = Join-Path $fixtureDirectory 'Assets/_OmniTwin/Scripts/Network/TwinWebSocketClient.cs'
$originalScript = [IO.File]::ReadAllText($clientPath)
$metaPath = $clientPath + '.meta'
$metaContent = "fileFormatVersion: 2`nguid: 0bf7c3b514c04734986e9de280452bba`n"
[IO.File]::WriteAllText($metaPath, $metaContent)
& $installer -ProjectPath $fixtureDirectory -DotNetPath $DotNetPath
if ([IO.File]::ReadAllText($metaPath) -ne $metaContent) { throw 'Reimport replaced an existing .meta file.' }
$recordHash = (Get-FileHash -LiteralPath $recordPath -Algorithm SHA256).Hash

[IO.File]::WriteAllText($clientPath, $originalScript + "`n// Local user customization retained by the importer test.`n")
$rejected = $false
try { & $installer -ProjectPath $fixtureDirectory -DotNetPath $DotNetPath }
catch { if ($_.Exception.Message -notlike '*locally edited file*') { throw }; $rejected = $true }
if (-not $rejected -or [IO.File]::ReadAllText($clientPath) -eq $originalScript) { throw 'Import did not protect local source changes.' }
if ((Get-FileHash -LiteralPath $recordPath -Algorithm SHA256).Hash -ne $recordHash) { throw 'Failed import altered the ownership record.' }

[IO.File]::WriteAllText($clientPath, $originalScript)
$duplicateDirectory = Join-Path $fixtureDirectory 'Assets/AnotherPlugin'
New-Item -ItemType Directory -Path $duplicateDirectory -Force | Out-Null
[IO.File]::Copy((Join-Path $fixtureDirectory 'Assets/_OmniTwin/Plugins/OmniTwin.Contracts.dll'),
    (Join-Path $duplicateDirectory 'OmniTwin.Contracts.dll'))
$rejected = $false
try { & $installer -ProjectPath $fixtureDirectory -DotNetPath $DotNetPath }
catch { if ($_.Exception.Message -notlike '*Duplicate assembly*') { throw }; $rejected = $true }
if (-not $rejected) { throw 'Duplicate assembly was not rejected.' }
Write-Output 'Unity import verified: real portable libraries and all seven scripts copied; repeat import preserves metadata; local edits and duplicate assemblies rejected.'
