function Restore-ProcessEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [hashtable] $Snapshot)

    foreach ($name in $Snapshot.Keys) {
        # PowerShell converts ordinary $null to an empty string for this .NET call.
        # Modern .NET preserves empty variables, which override Docker --env-file values.
        if ($null -eq $Snapshot[$name]) {
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($name, $Snapshot[$name], 'Process')
        }
    }
}
