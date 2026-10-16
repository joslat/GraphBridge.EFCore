#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Push-Location $candidateRepo
try {
    Invoke-CandidateCommand -Log fresh-sample.log -Executable (Join-Path $PSHOME 'pwsh.exe') -Arguments @('-NoProfile', '-File', 'eng/scripts/Run-BloggingQuickstart.ps1') -MinimumGiB 7
    Write-CandidateReceipt -Check fresh-sample -Log fresh-sample.log
} finally { Pop-Location }
