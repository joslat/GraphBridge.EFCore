#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Push-Location $candidateRepo
try {
    Invoke-CandidateCommand -Log dependency-audit.log -Executable (Join-Path $PSHOME 'pwsh.exe') -Arguments @('-NoProfile', '-File', 'eng/scripts/Export-DependencyInventory.ps1', '-Check') -MinimumGiB 5.5
    Invoke-CandidateCommand -Log vulnerabilities.json -Executable dotnet -Arguments @('list', 'GraphBridge.sln', 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--output-version', '1', '--no-restore') -MinimumGiB 5.5
    $audit = Get-Content -LiteralPath (Join-Path $candidateRoot 'vulnerabilities.json') -Raw | ConvertFrom-Json
    $findings = @($audit.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $null -ne $_ -and @($_.vulnerabilities).Count -gt 0 })
    if ($findings.Count -gt 0) { throw 'Candidate dependency advisory findings require review.' }
    Get-Content -LiteralPath (Join-Path $candidateRoot 'vulnerabilities.json') | Add-Content -LiteralPath (Join-Path $candidateRoot 'dependency-audit.log') -Encoding utf8
    Write-CandidateReceipt -Check dependency-audit -Log dependency-audit.log
} finally { Pop-Location }
