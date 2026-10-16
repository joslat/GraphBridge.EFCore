[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../eng/scripts/Restore-ProcessEnvironment.ps1')
$probePrefix = 'GRAPHBRIDGE_ENV_TEST_' + [Guid]::NewGuid().ToString('N')
$snapshot = @{ ($probePrefix + '_absent') = $null; ($probePrefix + '_empty') = ''; ($probePrefix + '_value') = 'original' }
try {
    foreach ($name in $snapshot.Keys) { [Environment]::SetEnvironmentVariable($name, 'temporary', 'Process') }
    Restore-ProcessEnvironment -Snapshot $snapshot
    $current = [Environment]::GetEnvironmentVariables('Process')
    if ($current.Contains($probePrefix + '_absent')) { throw 'Absent variable was restored as a present empty override.' }
    if ($current[$probePrefix + '_value'] -cne 'original') { throw 'Original populated value was not restored.' }
    # Framework-era Windows PowerShell deletes empty values; modern .NET preserves them.
    # Match this runtime's direct non-null empty-string behavior, without weakening absence.
    [Environment]::SetEnvironmentVariable($probePrefix + '_empty_control', '', 'Process')
    $current = [Environment]::GetEnvironmentVariables('Process')
    if ($current.Contains($probePrefix + '_empty') -ne $current.Contains($probePrefix + '_empty_control')) {
        throw 'Original empty value did not follow this runtime''s native empty-value semantics.'
    }
    Write-Output 'Process environment restoration passed: absent, populated and empty values; no persistent changes.'
}
finally {
    foreach ($name in @($snapshot.Keys) + @($probePrefix + '_empty_control')) {
        [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
    }
}
