# Source-only definitions. Importing this file does not accept a license or invoke Docker.
$enterpriseRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$enterpriseStateDirectory = Join-Path $enterpriseRepoRoot '.artifacts/enterprise-fixture'
$enterpriseEnvironmentPath = Join-Path $enterpriseStateDirectory 'fixture.env'
$enterpriseComposePath = Join-Path $PSScriptRoot 'docker-compose.yml'
$enterpriseApprovedImage = 'neo4j:2026.09.0-enterprise@sha256:595846b50b04d5f93a4850f501c6105412dc3ea7b6594126d361720b0f450a52'
. (Join-Path $enterpriseRepoRoot 'eng/scripts/Assert-FixtureResources.ps1')

function Assert-EnterpriseStartupSelection {
    param([string] $Edition, [string] $LicenseAcceptance, [string] $Image)
    if ($Edition -cne 'enterprise' -or $LicenseAcceptance -cnotin @('yes', 'eval') -or $Image -cne $enterpriseApprovedImage) {
        throw 'Explicit Enterprise edition, license choice (yes/commercial or eval/evaluation), and approved digest-pinned image are required.'
    }
}

function Read-EnterpriseFixtureEnvironment {
    if (-not (Test-Path -LiteralPath $enterpriseEnvironmentPath -PathType Leaf)) {
        throw 'No dedicated Enterprise fixture environment exists.'
    }
    $enterpriseValues = @{}
    foreach ($enterpriseLine in [System.IO.File]::ReadAllLines($enterpriseEnvironmentPath)) {
        if ($enterpriseLine -notmatch '^(GRAPHBRIDGE_ENTERPRISE_[A-Z0-9_]+)=(.*)$' -or $enterpriseValues.ContainsKey($Matches[1])) {
            throw 'Invalid or duplicate Enterprise fixture environment entry.'
        }
        $enterpriseValues[$Matches[1]] = $Matches[2]
    }
    $enterpriseRequiredKeys = @('INTEGRATION', 'EDITION', 'LICENSE', 'IMAGE', 'PROJECT', 'FIXTURE_ID', 'URI', 'DATABASE',
        'ADMIN_PASSWORD', 'READER_PASSWORD', 'WRITER_PASSWORD', 'NO_SCHEMA_PASSWORD') | ForEach-Object { 'GRAPHBRIDGE_ENTERPRISE_' + $_ }
    if ($enterpriseValues.Count -ne $enterpriseRequiredKeys.Count -or
        @($enterpriseRequiredKeys | Where-Object { -not $enterpriseValues.ContainsKey($_) }).Count -ne 0 -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_INTEGRATION -cne '1') {
        throw 'Enterprise fixture environment requires the complete exact opt-in key set.'
    }
    foreach ($enterpriseSecretKey in @('ADMIN_PASSWORD', 'READER_PASSWORD', 'WRITER_PASSWORD', 'NO_SCHEMA_PASSWORD')) {
        if ($enterpriseValues['GRAPHBRIDGE_ENTERPRISE_' + $enterpriseSecretKey] -cnotmatch '^[A-Fa-f0-9]{64}$') {
            throw 'Enterprise fixture credentials must be generated 256-bit hexadecimal secrets.'
        }
    }
    if ($enterpriseValues.GRAPHBRIDGE_ENTERPRISE_PROJECT -cnotmatch '^graphbridge-enterprise-[a-f0-9]{32}$' -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID -cnotmatch '^[a-f0-9]{32}$' -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_PROJECT -cne ('graphbridge-enterprise-' + $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID) -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_IMAGE -cne $enterpriseApprovedImage -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_EDITION -cne 'enterprise' -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_LICENSE -cnotin @('yes', 'eval') -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_URI -cne 'bolt://127.0.0.1:18687' -or
        $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_DATABASE -cne 'neo4j') {
        throw 'Enterprise fixture identity, endpoint, edition, or pinned image is invalid.'
    }
    return $enterpriseValues
}

function Assert-EnterpriseFixtureResources {
    param([switch] $Startup, [hashtable] $EnvironmentValues,
        # Offline policy checks inject synthetic snapshots; entry scripts do not expose this seam.
        [scriptblock] $SnapshotProvider = { Get-FixtureResourceSnapshot })
    $enterpriseSnapshot = & $SnapshotProvider
    if ($null -eq $enterpriseSnapshot -or $null -eq $enterpriseSnapshot.AvailableMemoryBytes -or
        [long]$enterpriseSnapshot.AvailableMemoryBytes -le 0 -or $null -eq $enterpriseSnapshot.RunningContainers) {
        throw 'Enterprise resource inspection is incomplete; workload is deferred.'
    }
    $enterpriseReserve = 5GB
    if (Test-FixtureDedicatedHostedRunner) { $enterpriseReserve = 2GB }
    $enterpriseRequired = $enterpriseReserve + 512MB
    if ($Startup) { $enterpriseRequired += 2GB }
    if ([long]$enterpriseSnapshot.AvailableMemoryBytes -lt $enterpriseRequired) {
        throw ('Enterprise work deferred: {0:F2} GiB available; {1:F2} GiB required including reserve and bounded workload headroom.' -f
            ($enterpriseSnapshot.AvailableMemoryBytes / 1GB), ($enterpriseRequired / 1GB))
    }
    if (-not $Startup) {
        if ($null -eq $EnvironmentValues) { throw 'Enterprise ownership must be selected explicitly.' }
        $enterpriseOwned = @($enterpriseSnapshot.RunningContainers | Where-Object {
            $_.Project -ceq $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_PROJECT -and $_.Service -ceq 'neo4j' -and $_.Disposable -ceq 'true'
        })
        if ($enterpriseOwned.Count -ne 1) { throw 'Selected Enterprise service ownership is missing or ambiguous.' }
        # Inspect only labels and configured image; never emit the container environment or credentials.
        $enterpriseIdentity = @(& docker inspect --format '{{index .Config.Labels "org.graphbridge.enterprise"}}|{{index .Config.Labels "org.graphbridge.fixture.id"}}|{{.Config.Image}}' $enterpriseOwned[0].Id)
        if ($LASTEXITCODE -ne 0 -or $enterpriseIdentity.Count -ne 1 -or
            $enterpriseIdentity[0] -cne ('true|' + $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID + '|' + $enterpriseApprovedImage)) {
            throw 'Selected container is not the exact owned Enterprise fixture.'
        }
    }
}

function Lock-EnterpriseFixture {
    [System.IO.Directory]::CreateDirectory($enterpriseStateDirectory) | Out-Null
    try {
        return [System.IO.File]::Open((Join-Path $enterpriseStateDirectory 'operation.lock'),
            [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    }
    catch { throw 'Another Enterprise fixture operation is active; serialize all database tests and heavy work through the primary agent.' }
}

function New-EnterpriseFixtureSecret {
    $enterpriseBytes = [byte[]]::new(32)
    $enterpriseRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $enterpriseRandom.GetBytes($enterpriseBytes) }
    finally { $enterpriseRandom.Dispose() }
    return [BitConverter]::ToString($enterpriseBytes).Replace('-', '')
}

function Invoke-EnterpriseCompose {
    param(
        [Parameter(Mandatory = $true)][hashtable] $EnvironmentValues,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [string] $InputText,
        [switch] $SuperviseStartup
    )
    # Compose gives inherited process values precedence over --env-file. Pin every generated input
    # for this invocation so an inherited license/image/secret cannot override the explicit fixture.
    $enterpriseSaved = @{}
    try {
        foreach ($enterpriseName in $EnvironmentValues.Keys) {
            $enterpriseSaved[$enterpriseName] = [Environment]::GetEnvironmentVariable($enterpriseName, 'Process')
            [Environment]::SetEnvironmentVariable($enterpriseName, $EnvironmentValues[$enterpriseName], 'Process')
        }
        if ($SuperviseStartup) {
            Invoke-EnterpriseSupervisedProcess -FilePath 'docker' -Arguments (@('compose', '--project-name',
                $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_PROJECT, '-f', $enterpriseComposePath,
                '--env-file', $enterpriseEnvironmentPath) + $Arguments) -LogStem 'startup'
        }
        elseif ($PSBoundParameters.ContainsKey('InputText')) {
            $InputText | & docker compose --project-name $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_PROJECT -f $enterpriseComposePath --env-file $enterpriseEnvironmentPath @Arguments
        }
        else {
            & docker compose --project-name $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_PROJECT -f $enterpriseComposePath --env-file $enterpriseEnvironmentPath @Arguments
        }
        if (-not $SuperviseStartup -and $LASTEXITCODE -ne 0) { throw 'The selected owned Enterprise Compose operation failed; credentials were not printed.' }
    }
    finally {
        foreach ($enterpriseName in $enterpriseSaved.Keys) {
            [Environment]::SetEnvironmentVariable($enterpriseName, $enterpriseSaved[$enterpriseName], 'Process')
        }
    }
}

function Invoke-EnterpriseSupervisedProcess {
    param([Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][ValidateSet('startup', 'test')][string] $LogStem)
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Enterprise supervised workloads require PowerShell 7+ for owned process-tree cleanup.' }
    $enterpriseChild = $null
    $enterpriseOperationFailed = $false
    try {
        if (@($Arguments | Where-Object { $_.Contains('"') -or $_.Contains("`n") -or $_.Contains("`r") }).Count -ne 0) {
            throw 'Unexpected quoted or multiline workload argument.'
        }
        $enterpriseStart = @{
            FilePath = $FilePath
            ArgumentList = (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
            WorkingDirectory = $enterpriseRepoRoot
            RedirectStandardOutput = (Join-Path $enterpriseStateDirectory ($LogStem + '.stdout.log'))
            RedirectStandardError = (Join-Path $enterpriseStateDirectory ($LogStem + '.stderr.log'))
            PassThru = $true
        }
        if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { $enterpriseStart.WindowStyle = 'Hidden' }
        $enterpriseChild = Start-Process @enterpriseStart
        while (-not $enterpriseChild.WaitForExit(5000)) {
            $enterpriseReserve = 5GB
            if (Test-FixtureDedicatedHostedRunner) { $enterpriseReserve = 2GB }
            if ((Get-FixtureAvailableMemoryBytes) -lt $enterpriseReserve) {
                throw 'Owned Enterprise workload interrupted because host RAM fell below the shared reserve.'
            }
        }
        $enterpriseChild.Refresh()
        Get-Content -LiteralPath $enterpriseStart.RedirectStandardOutput
        Get-Content -LiteralPath $enterpriseStart.RedirectStandardError
        if ($enterpriseChild.ExitCode -ne 0) { throw ('Owned Enterprise workload failed with exit code ' + $enterpriseChild.ExitCode + '. No acceptance gate is closed.') }
    }
    catch { $enterpriseOperationFailed = $true; throw }
    finally {
        if ($null -ne $enterpriseChild) {
            try {
                if (-not $enterpriseChild.HasExited) {
                    $enterpriseChild.Kill($true)
                    if (-not $enterpriseChild.WaitForExit(30000)) { throw 'Owned workload did not terminate within the cleanup deadline.' }
                }
            }
            catch {
                if (-not $enterpriseOperationFailed) { throw }
                Write-Warning 'Owned workload cleanup could not be confirmed; the original workload failure is preserved.'
            }
            finally { $enterpriseChild.Dispose() }
        }
    }
}

function Get-EnterpriseTeardownContainer {
    param([Parameter(Mandatory = $true)][hashtable] $EnvironmentValues)
    $enterpriseContainers = @(& docker ps -a --no-trunc --filter ('label=com.docker.compose.project=' + $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_PROJECT) --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0 -or $enterpriseContainers.Count -gt 1) { throw 'Enterprise teardown selection is ambiguous; nothing stopped.' }
    if ($enterpriseContainers.Count -eq 0) { return $null }
    if ($enterpriseContainers[0] -cnotmatch '^[a-f0-9]{64}$') { throw 'Enterprise container identity is invalid.' }
    $enterpriseIdentity = @(& docker inspect --format '{{index .Config.Labels "org.graphbridge.disposable"}}|{{index .Config.Labels "org.graphbridge.enterprise"}}|{{index .Config.Labels "org.graphbridge.fixture.id"}}|{{.Config.Image}}' $enterpriseContainers[0])
    if ($LASTEXITCODE -ne 0 -or $enterpriseIdentity.Count -ne 1 -or
        $enterpriseIdentity[0] -cne ('true|true|' + $EnvironmentValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID + '|' + $enterpriseApprovedImage)) {
        throw 'Enterprise teardown ownership mismatch; nothing stopped.'
    }
    return $enterpriseContainers[0]
}
