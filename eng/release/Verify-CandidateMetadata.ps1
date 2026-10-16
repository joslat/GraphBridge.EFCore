#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
$arguments = @('eng/release/Create-ReleaseManifest.py', '--commit', $candidateCommit, '--version', $candidateVersion,
    '--output-dir', (Join-Path $candidateRoot 'metadata-preflight'))
foreach ($package in Get-ChildItem -LiteralPath $candidateFeed -Filter '*.nupkg') { $arguments += @('--package', $package.FullName) }
foreach ($receipt in Get-ChildItem -LiteralPath (Join-Path $candidateRoot 'receipts') -Filter '*.json') { $arguments += @('--receipt', $receipt.FullName) }
& python @arguments
if ($LASTEXITCODE -notin @(0, 3)) { throw 'Candidate package metadata/provenance validation failed.' }
Write-Output 'Candidate metadata validated; status remains incomplete until every required fresh receipt passes.'
& python eng/release/test_release_manifest.py
if ($LASTEXITCODE) { throw 'Candidate manifest adversarial checks failed.' }
