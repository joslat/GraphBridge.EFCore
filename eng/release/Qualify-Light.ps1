#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Push-Location $candidateRepo
try {
    Invoke-CandidateCommand -Log public-documents.log -Executable python -Arguments @('eng/release/Check-PublicDocuments.py') -MinimumGiB 5.2
    Write-CandidateReceipt -Check public-documents -Log public-documents.log
    $tree = Join-Path $candidateRoot 'tracked-tree'
    if (-not (Test-Path -LiteralPath (Join-Path $tree 'README.md'))) {
        New-Item -ItemType Directory -Force -Path $tree | Out-Null
        $archivePath = Join-Path $candidateRoot 'source.tar'
        & git archive --format=tar "--output=$archivePath" HEAD
        if ($LASTEXITCODE) { throw 'Source archive failed.' }
        & tar -xf (Join-Path $candidateRoot 'source.tar') -C $tree
        if ($LASTEXITCODE) { throw 'Source extraction failed.' }
    }
    $env:GOMEMLIMIT = '128MiB'; $env:GOMAXPROCS = '2'
    $scanner = Join-Path $candidateRepo '.artifacts/tools/gitleaks-8.30.1/bin/gitleaks.exe'
    Invoke-CandidateCommand -Log secret-scan.log -Executable $scanner -Arguments @('version') -MinimumGiB 5.4
    Invoke-CandidateCommand -Log secret-scan.log -Executable $scanner -Arguments @('git', '.', '--log-opts=--all', '--config=.gitleaks.toml', '--redact=100', '--report-format=json', "--report-path=$(Join-Path $candidateRoot 'history.json')", '--no-banner') -MinimumGiB 5.4
    Invoke-CandidateCommand -Log secret-scan.log -Executable $scanner -Arguments @('dir', $tree, '--config=.gitleaks.toml', '--redact=100', '--report-format=json', "--report-path=$(Join-Path $candidateRoot 'tracked-tree.json')", '--no-banner') -MinimumGiB 5.4
    Write-CandidateReceipt -Check secret-scan -Log secret-scan.log
} finally { Pop-Location }
