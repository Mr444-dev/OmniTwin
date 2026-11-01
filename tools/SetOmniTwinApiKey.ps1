<#
.SYNOPSIS
Sets, inspects, or removes one OmniTwin API key in the current PowerShell process.
.DESCRIPTION
Set reads a masked prompt. The key is not a command argument, printed result, or saved file.
Child processes launched from this terminal inherit the variable. Existing processes are unaffected.
The backend uses Meta for perception/voice and Baseten when the operator requests Brain policy synthesis.
Setting either key alone does not send a provider request.
.PARAMETER Provider
Meta selects META_API_KEY; Baseten selects BASETEN_API_KEY.
.PARAMETER Action
Set prompts for a key. Status prints presence only. Clear removes the process variable.
#>
[CmdletBinding()]
param(
    [ValidateSet('Meta', 'Baseten')][string]$Provider = 'Meta',
    [ValidateSet('Set', 'Status', 'Clear')][string]$Action = 'Set'
)

$ErrorActionPreference = 'Stop'
$variableName = if ($Provider -eq 'Meta') { 'META_API_KEY' } else { 'BASETEN_API_KEY' }

if ($Action -eq 'Status') {
    [pscustomobject]@{
        Variable = $variableName
        IsSet = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variableName, 'Process'))
        ConsumedByCurrentBackend = $true
        Scope = 'Current PowerShell process and new child processes'
    }
    return
}
if ($Action -eq 'Clear') {
    [Environment]::SetEnvironmentVariable($variableName, $null, 'Process')
    Write-Host "$variableName removed from this terminal. Existing child processes retain their environment."
    return
}

$secureKey = Read-Host "Paste $variableName (input is masked)" -AsSecureString
try {
    $plainKey = [Net.NetworkCredential]::new('', $secureKey).Password
    if ([string]::IsNullOrWhiteSpace($plainKey) -or $plainKey.Length -gt 8192 -or
        $plainKey -ne $plainKey.Trim() -or $plainKey.IndexOfAny([char[]]"`r`n") -ge 0) {
        throw 'Enter one nonempty key without surrounding whitespace or line breaks. The existing variable was preserved.'
    }
    [Environment]::SetEnvironmentVariable($variableName, $plainKey, 'Process')
} finally {
    $secureKey.Dispose()
    Remove-Variable -Name plainKey -ErrorAction SilentlyContinue
}
Write-Host "$variableName set for this terminal and new child processes. No key file was written."
if ($Provider -eq 'Baseten') {
    Write-Host 'BASETEN_API_KEY is used when you explicitly request Brain policy synthesis; setting it alone sends no provider request.'
}
