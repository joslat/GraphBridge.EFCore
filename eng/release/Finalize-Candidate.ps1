#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Assert-CandidateSource
$checks = @('locked-build','provider-unit','provider-live','specification','tooling','generator','template-consumer','combined-package-consumer','fresh-sample','secret-scan','dependency-audit','public-documents','windows-consumer','linux-consumer')
foreach ($check in $checks) { if (-not (Test-CandidateReceipt -Check $check)) { throw "Missing/stale candidate receipt: $check" } }
$hashes = Get-Content -LiteralPath (Join-Path $candidateRoot 'package-hashes.json') -Raw | ConvertFrom-Json
foreach ($entry in $hashes) {
    if ((Get-FileHash -LiteralPath (Join-Path $candidateFeed $entry.file) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Frozen package bytes changed during qualification.' }
}
$basis = @(& rg --files --hidden --no-ignore tests eng -g '!**/bin/**' -g '!**/obj/**' -g '!**/.artifacts/**' -g '*.cs' -g '*.csproj' -g '*.ps1' -g '*.py' -g 'packages.lock.json') | Sort-Object
$inventory = @($basis | ForEach-Object { [ordered]@{ path = $_.Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $candidateRoot 'acceptance-source-inventory.json') -Encoding utf8
$arguments = @('eng/release/Create-ReleaseManifest.py', '--commit', $candidateCommit, '--version', $candidateVersion,
    '--output-dir', (Join-Path $candidateRoot 'qualified'))
foreach ($package in Get-ChildItem -LiteralPath $candidateFeed -Filter '*.nupkg') { $arguments += @('--package', $package.FullName) }
foreach ($receipt in Get-ChildItem -LiteralPath (Join-Path $candidateRoot 'receipts') -Filter '*.json') { $arguments += @('--receipt', $receipt.FullName) }
& python @arguments
if ($LASTEXITCODE) { throw 'Final candidate manifest did not qualify.' }
