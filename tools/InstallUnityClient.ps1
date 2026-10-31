<#
.SYNOPSIS
Builds the portable transport and imports the client into an existing Unity 6.6 URP project.
.DESCRIPTION
Does not create projects or edit package manifests. Close the target Editor first.
Reimports preserve .meta files and reject local edits to previously imported files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDirectory = [IO.Path]::GetFullPath($ProjectPath)
$versionPath = Join-Path $projectDirectory 'ProjectSettings/ProjectVersion.txt'
$packagePath = Join-Path $projectDirectory 'Packages/manifest.json'
$assetsDirectory = Join-Path $projectDirectory 'Assets'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $assetsDirectory -PathType Container)) {
    throw 'ProjectPath must point to an existing Unity project created in the Hub.'
}
if ((Get-Content -LiteralPath $versionPath -Raw) -notmatch 'm_EditorVersion:\s*6000\.6\.\d+f\d+') {
    throw 'This import targets stable Unity 6.6. Use the Editor version documented in docs/unity-setup.md.'
}
$packages = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
if (-not $packages.dependencies.'com.unity.render-pipelines.universal') { throw 'The target must use the Universal 3D / URP template.' }
if (Test-Path -LiteralPath (Join-Path $projectDirectory 'Temp/UnityLockfile')) { throw 'Close this project in Unity before importing.' }

$dotnetExecutable = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop).Source
$projects = @('OmniTwin.Transport', 'OmniTwin.Policies')
$libraries = [Collections.Generic.List[string]]::new()
Push-Location $repoDirectory
try {
    foreach ($projectName in $projects) {
        $portableProject = Join-Path $repoDirectory ('src/' + $projectName + '/' + $projectName + '.csproj')
        & $dotnetExecutable restore $portableProject --locked-mode --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Locked portable restore failed: $projectName" }
        & $dotnetExecutable build $portableProject -c Release -f netstandard2.1 --no-restore --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Portable build failed: $projectName" }
        $metadataText = & $dotnetExecutable msbuild $portableProject -target:ResolveReferences `
            -property:TargetFramework=netstandard2.1 -property:Configuration=Release -property:CopyLocalLockFileAssemblies=true `
            -getItem:ReferenceCopyLocalPaths -getProperty:TargetPath -nologo
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve portable dependencies: $projectName" }
        $metadata = ($metadataText -join "`n") | ConvertFrom-Json
        $libraries.Add($metadata.Properties.TargetPath)
        foreach ($reference in $metadata.Items.ReferenceCopyLocalPaths) {
            if ($reference.Identity -like '*.dll' -and -not $libraries.Contains($reference.Identity)) { $libraries.Add($reference.Identity) }
        }
    }
}
finally { Pop-Location }

$plan = [Collections.Generic.List[object]]::new()
foreach ($library in ($libraries | Sort-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw "Resolved library is missing: $library" }
    $destination = 'Assets/_OmniTwin/Plugins/' + [IO.Path]::GetFileName($library)
    $previous = @($plan | Where-Object path -eq $destination)
    if ($previous.Count -gt 0) {
        if ((Get-FileHash -LiteralPath $previous[0].source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash) {
            throw "Conflicting portable dependency: $destination"
        }
        continue
    }
    $plan.Add([pscustomobject]@{ source = $library; path = $destination })
}
$sourceDirectory = Join-Path $repoDirectory 'unity/client/Runtime'
foreach ($script in (Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File -Recurse)) {
    $relative = $script.FullName.Substring($sourceDirectory.Length + 1).Replace('\', '/')
    $plan.Add([pscustomobject]@{ source = $script.FullName; path = 'Assets/_OmniTwin/Scripts/' + $relative })
}
if ($plan.Count -lt 4 -or @($plan | Group-Object path | Where-Object { $_.Count -ne 1 }).Count -ne 0) {
    throw 'The import plan is incomplete or contains duplicate destinations.'
}

$recordPath = Join-Path $projectDirectory 'OmniTwinClient.import.json'
$owned = @{}
if (Test-Path -LiteralPath $recordPath) {
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ($record.schemaVersion -ne 1) { throw 'Unknown client import record version.' }
    foreach ($entry in $record.files) { $owned[$entry.path] = $entry.sha256 }
}

# Validate every destination before writing any project content.
foreach ($entry in $plan) {
    $entry | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-FileHash -LiteralPath $entry.source -Algorithm SHA256).Hash
    $target = Join-Path $projectDirectory $entry.path
    if (Test-Path -LiteralPath $target) {
        $existing = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($existing -ne $entry.sha256 -and (-not $owned.ContainsKey($entry.path) -or $existing -ne $owned[$entry.path])) {
            throw "Import would overwrite an unowned or locally edited file: $($entry.path)"
        }
    }
}
$plannedLibraries = @{}
foreach ($entry in $plan | Where-Object { $_.path -like '*.dll' }) { $plannedLibraries[[IO.Path]::GetFileName($entry.path)] = [IO.Path]::GetFullPath((Join-Path $projectDirectory $entry.path)) }
foreach ($existing in (Get-ChildItem -LiteralPath $assetsDirectory -Filter '*.dll' -File -Recurse)) {
    if ($plannedLibraries.ContainsKey($existing.Name) -and $existing.FullName -ne $plannedLibraries[$existing.Name]) {
        throw "Duplicate assembly already exists outside the import destination: $($existing.FullName)"
    }
}
if ($owned.Keys.Count -ne 0) {
    $newPaths = @($plan | ForEach-Object { $_.path })
    foreach ($oldPath in $owned.Keys) {
        if ($newPaths -notcontains $oldPath) { throw "Obsolete imported file needs manual review before updating: $oldPath" }
    }
}

foreach ($entry in $plan) {
    $target = Join-Path $projectDirectory $entry.path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    [IO.File]::Copy($entry.source, $target, $true)
}
$record = [ordered]@{
    schemaVersion = 1
    targetFramework = 'netstandard2.1'
    files = @($plan | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
}
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output "Imported $($plan.Count) files into $projectDirectory."
Write-Output 'Open the project, wait for compilation and .meta creation, then follow docs/unity-client.md.'
