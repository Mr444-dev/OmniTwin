<#
.SYNOPSIS
Builds the portable transport and imports the client into an existing Unity 6.6 URP project.
.DESCRIPTION
Does not create projects or edit package manifests. By default close the target Editor first.
-LiveEditor applies the same ownership-checked plan through that project's Unity AssetDatabase.
Reimports preserve .meta files and reject local edits to previously imported files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$DotNetPath = 'dotnet',
    [string]$UnityCliPath = 'unity',
    [switch]$LiveEditor
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
$unityLock = Join-Path $projectDirectory 'Temp/UnityLockfile'
if ((Test-Path -LiteralPath $unityLock) -and -not $LiveEditor) { throw 'Close this project in Unity before importing, or pass -LiveEditor to use its AssetDatabase.' }
if ($LiveEditor -and -not (Test-Path -LiteralPath $unityLock)) { throw '-LiveEditor requires this exact project to be open in Unity.' }

$dotnetExecutable = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop).Source
$projects = @('OmniTwin.Transport', 'OmniTwin.Policies', 'OmniTwin.Perception')
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

$record = [ordered]@{
    schemaVersion = 1
    targetFramework = 'netstandard2.1'
    files = @($plan | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
}
$recordJson = $record | ConvertTo-Json -Depth 5
if ($LiveEditor) {
    $unityExecutable = (Get-Command $UnityCliPath -CommandType Application -ErrorAction Stop).Source
    $recordBytes = [Text.UTF8Encoding]::new($false).GetBytes($recordJson)
    $recordBase64 = [Convert]::ToBase64String($recordBytes)
    $expectedRecordHash = if (Test-Path -LiteralPath $recordPath -PathType Leaf) { (Get-FileHash -LiteralPath $recordPath -Algorithm SHA256).Hash } else { '' }
    $tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('OmniTwin-live-import-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDirectory | Out-Null
    $scriptPath = Join-Path $tempDirectory 'ImportOmniTwin.cs'
    $toCSharpLiteral = { param([string]$value) ConvertTo-Json -InputObject $value -Compress }
    $sourcesLiteral = (@($plan | ForEach-Object { & $toCSharpLiteral $_.source }) -join ', ')
    $pathsLiteral = (@($plan | ForEach-Object { & $toCSharpLiteral $_.path }) -join ', ')
    $hashesLiteral = (@($plan | ForEach-Object { & $toCSharpLiteral $_.sha256 }) -join ', ')
    $ownedPathsLiteral = (@($owned.Keys | ForEach-Object { & $toCSharpLiteral ([string]$_) }) -join ', ')
    $ownedHashesLiteral = (@($owned.Keys | ForEach-Object { & $toCSharpLiteral ([string]$owned[$_]) }) -join ', ')
    $liveScript = @"
var projectRoot = System.IO.Path.GetFullPath($(& $toCSharpLiteral $projectDirectory));
var activeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."));
if (!string.Equals(projectRoot, activeRoot, System.StringComparison.OrdinalIgnoreCase)) throw new System.InvalidOperationException("Unity project path changed.");
var recordPath = System.IO.Path.GetFullPath($(& $toCSharpLiteral $recordPath));
var expectedRecordHash = $(& $toCSharpLiteral $expectedRecordHash);
var previousRecord = System.IO.File.Exists(recordPath) ? System.IO.File.ReadAllBytes(recordPath) : null;
System.Func<byte[], string> hash = bytes => { using (var sha = System.Security.Cryptography.SHA256.Create()) return System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty); };
if ((previousRecord == null ? "" : hash(previousRecord)) != expectedRecordHash) throw new System.InvalidOperationException("Import ownership record changed before live import.");
var sources = new string[] { $sourcesLiteral };
var paths = new string[] { $pathsLiteral };
var hashes = new string[] { $hashesLiteral };
var ownedPaths = new string[] { $ownedPathsLiteral };
var ownedHashes = new string[] { $ownedHashesLiteral };
var owned = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < ownedPaths.Length; i++) owned.Add(ownedPaths[i], ownedHashes[i]);
var assetRoot = System.IO.Path.GetFullPath(UnityEngine.Application.dataPath) + System.IO.Path.DirectorySeparatorChar;
var entries = new System.Collections.Generic.List<(string Source, string Target, string Relative)>();
long totalBytes = 0;
for (var i = 0; i < sources.Length; i++)
{
    var source = System.IO.Path.GetFullPath(sources[i]);
    var relative = paths[i];
    if (!relative.StartsWith("Assets/_OmniTwin/", System.StringComparison.Ordinal) || relative.Contains("..")) throw new System.InvalidOperationException("Invalid import destination.");
    var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    if (!target.StartsWith(assetRoot, System.StringComparison.OrdinalIgnoreCase)) throw new System.InvalidOperationException("Destination escaped Assets.");
    var sourceBytes = System.IO.File.ReadAllBytes(source); totalBytes += sourceBytes.Length;
    if (sourceBytes.Length > 16 * 1024 * 1024 || totalBytes > 64 * 1024 * 1024 || hash(sourceBytes) != hashes[i]) throw new System.InvalidOperationException("Import source changed or exceeded limits.");
    var exists = System.IO.File.Exists(target);
    string oldHash;
    var existingHash = exists ? hash(System.IO.File.ReadAllBytes(target)) : null;
    if (exists && existingHash != hashes[i] && (!owned.TryGetValue(relative, out oldHash) || existingHash != oldHash)) throw new System.InvalidOperationException("A destination is unowned or locally edited: " + relative);
    if (!exists && owned.ContainsKey(relative)) throw new System.InvalidOperationException("An owned destination disappeared before live import: " + relative);
    entries.Add((source, target, relative));
}
var backups = new System.Collections.Generic.Dictionary<string, byte[]>(System.StringComparer.OrdinalIgnoreCase);
foreach (var entry in entries) if (System.IO.File.Exists(entry.Target)) backups.Add(entry.Target, System.IO.File.ReadAllBytes(entry.Target));
UnityEditor.AssetDatabase.DisallowAutoRefresh();
var editing = false;
try
{
    UnityEditor.AssetDatabase.StartAssetEditing(); editing = true;
    try
    {
        foreach (var entry in entries) { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry.Target)); System.IO.File.Copy(entry.Source, entry.Target, true); }
        System.IO.File.WriteAllBytes(recordPath, System.Convert.FromBase64String("$recordBase64"));
    }
    catch
    {
        foreach (var entry in entries)
        {
            byte[] backup;
            if (backups.TryGetValue(entry.Target, out backup)) System.IO.File.WriteAllBytes(entry.Target, backup);
            else if (System.IO.File.Exists(entry.Target)) System.IO.File.Delete(entry.Target);
        }
        if (previousRecord == null) { if (System.IO.File.Exists(recordPath)) System.IO.File.Delete(recordPath); } else System.IO.File.WriteAllBytes(recordPath, previousRecord);
        throw;
    }
}
finally { try { if (editing) UnityEditor.AssetDatabase.StopAssetEditing(); } finally { UnityEditor.AssetDatabase.AllowAutoRefresh(); } }
UnityEngine.Debug.Log("OmniTwin live import validated; existing .meta files were retained.");
return "Imported " + entries.Count + " files through Unity AssetDatabase.";
"@
    [IO.File]::WriteAllText($scriptPath, $liveScript, [Text.UTF8Encoding]::new($false))
    $cliOutput = & $unityExecutable command script eval_file --project-path $projectDirectory --caller plugin --skill unity-cli `
        --file $scriptPath --timeout 60 --format json --no-banner
    if ($LASTEXITCODE -ne 0) { throw "Unity live import command failed: $($cliOutput -join "`n")" }
    try { $cliResult = ($cliOutput -join "`n") | ConvertFrom-Json } catch { throw 'Unity live import returned invalid JSON.' }
    if (-not $cliResult.success -or -not $cliResult.data.result.success) { throw "Unity live import was rejected: $($cliResult | ConvertTo-Json -Depth 6 -Compress)" }
    Write-Output $cliResult.data.result.result
}
else {
    foreach ($entry in $plan) {
        $target = Join-Path $projectDirectory $entry.path
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [IO.File]::Copy($entry.source, $target, $true)
    }
    [IO.File]::WriteAllText($recordPath, $recordJson, [Text.UTF8Encoding]::new($false))
}
Write-Output "Imported $($plan.Count) files into $projectDirectory."
if ($LiveEditor) { Write-Output 'Unity AssetDatabase is importing the synchronized files; wait for C# compilation before entering Play mode.' }
else { Write-Output 'Open the project, wait for compilation and .meta creation, then follow docs/unity-client.md.' }
