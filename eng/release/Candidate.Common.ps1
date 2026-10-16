#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE = '1'
$candidateRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $env:GRAPHBRIDGE_CANDIDATE_COMMIT -or -not $env:GRAPHBRIDGE_CANDIDATE_VERSION) {
    throw 'Set GRAPHBRIDGE_CANDIDATE_COMMIT and GRAPHBRIDGE_CANDIDATE_VERSION explicitly; qualification never reuses an implicit historical candidate.'
}
$candidateCommit = (& git -C $candidateRepo rev-parse "$($env:GRAPHBRIDGE_CANDIDATE_COMMIT)^{commit}").Trim()
if ($LASTEXITCODE -or $candidateCommit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid candidate commit.' }
$candidateVersion = $env:GRAPHBRIDGE_CANDIDATE_VERSION
if ($candidateVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid candidate version.' }
$candidateRoot = Join-Path $candidateRepo ".artifacts/release/candidate-$candidateCommit-$candidateVersion"
$candidateFeed = Join-Path $candidateRoot 'packages'
New-Item -ItemType Directory -Force -Path $candidateRoot, $candidateFeed, (Join-Path $candidateRoot 'receipts') | Out-Null
. (Join-Path $candidateRepo 'eng/scripts/Assert-FixtureResources.ps1')

function Assert-CandidateSource {
    if ((& git -C $candidateRepo rev-parse HEAD).Trim() -cne $candidateCommit -or
        (& git -C $candidateRepo status --porcelain --untracked-files=normal)) {
        throw 'Candidate execution requires the exact clean frozen source commit.'
    }
}
function Invoke-CandidateCommand {
    param([string] $Log, [string] $Executable, [string[]] $Arguments, [double] $MinimumGiB = 6)
    Assert-CandidateSource
    if ((Get-FixtureAvailableMemoryBytes) -lt ($MinimumGiB * 1GB)) { throw 'Candidate workload deferred: insufficient measured RAM headroom.' }
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $candidateRoot $Log) -Append
    if ($LASTEXITCODE) { throw "Candidate command failed: $Executable (exit $LASTEXITCODE); see $Log." }
    if ((Get-FixtureAvailableMemoryBytes) -lt 5GB) { throw 'Candidate workload reached shared memory reserve.' }
    Assert-CandidateSource
}
function Write-CandidateReceipt {
    param([string] $Check, [string] $Log, [switch] $Packages)
    Assert-CandidateSource
    $path = Join-Path $candidateRoot $Log
    $receipt = [ordered]@{
        check = $Check; status = 'pass'; sourceCommit = $candidateCommit; sourceDirty = $false;
        evidencePath = [IO.Path]::GetRelativePath($candidateRepo, $path).Replace('\', '/');
        evidenceSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($Packages) {
        $files = @(Get-ChildItem -LiteralPath $candidateFeed -Filter '*.nupkg')
        if ($files.Count -ne 4) { throw 'Expected exactly four frozen candidate packages.' }
        $receipt.packageSha256 = @($files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $candidateRoot "receipts/$Check.json") -Encoding utf8
    Write-Output "Candidate receipt passed: $Check"
}

function Test-CandidateReceipt {
    param([string] $Check)
    $path = Join-Path $candidateRoot "receipts/$Check.json"
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $receipt = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($receipt.sourceCommit -cne $candidateCommit -or $receipt.sourceDirty -ne $false -or $receipt.status -ne 'pass') { return $false }
    $log = Join-Path $candidateRepo $receipt.evidencePath
    if (-not (Test-Path -LiteralPath $log) -or (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.evidenceSha256) { return $false }
    if ($receipt.packageSha256) {
        $hashes = @(Get-ChildItem -LiteralPath $candidateFeed -Filter '*.nupkg' | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        if (@(Compare-Object $hashes @($receipt.packageSha256)).Count) { return $false }
    }
    return $true
}
