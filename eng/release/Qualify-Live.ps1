#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Push-Location $candidateRepo
$owned = $false
try {
    if (Test-Path -LiteralPath (Join-Path $candidateRepo 'eng/.fixture.env')) { throw 'Qualification requires its own fixture lifecycle; an existing marker was found.' }
    $owned = $true
    & "$candidateRepo/eng/scripts/Start-Neo4jFixture.ps1"
    foreach ($suite in @(@{ Name='provider-live'; Suite='Integration' }, @{ Name='specification'; Suite='Specification' }, @{ Name='tooling-live'; Suite='Tooling' })) {
        if (Test-CandidateReceipt -Check $suite.Name) { continue }
        if ($suite.Name -eq 'tooling-live' -and (Test-CandidateReceipt -Check tooling)) { continue }
        Invoke-CandidateCommand -Log ($suite.Name + '.log') -Executable (Join-Path $PSHOME 'pwsh.exe') -Arguments @('-NoProfile', '-File', 'eng/scripts/Run-IntegrationTests.ps1', '-Suite', $suite.Suite, '-NoBuild', '-Verbosity', 'minimal', '-ResultsDirectory', $candidateRoot) -MinimumGiB 5.5
        [xml] $result = Get-Content -LiteralPath (Join-Path $candidateRoot ($suite.Suite + '.trx')) -Raw
        $counters = $result.SelectSingleNode("//*[local-name()='Counters']")
        if ([int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total) { throw 'Candidate live suite has skipped, missing or failed cases.' }
        if ($suite.Name -ne 'tooling-live') { Write-CandidateReceipt -Check $suite.Name -Log ($suite.Name + '.log') }
    }
    Get-Content -LiteralPath (Join-Path $candidateRoot 'tooling-offline.log'), (Join-Path $candidateRoot 'tooling-live.log') | Set-Content -LiteralPath (Join-Path $candidateRoot 'tooling.log') -Encoding utf8
    Write-CandidateReceipt -Check tooling -Log tooling.log
    Invoke-CandidateCommand -Log combined-package-consumer.log -Executable (Join-Path $PSHOME 'pwsh.exe') -Arguments @('-NoProfile', '-File', 'eng/scripts/Test-CombinedPackages.ps1', '-Live', '-CandidateFeed', $candidateFeed, '-CandidateVersion', $candidateVersion) -MinimumGiB 6
    Write-CandidateReceipt -Check combined-package-consumer -Log combined-package-consumer.log -Packages
    Invoke-CandidateCommand -Log template-consumer.log -Executable (Join-Path $PSHOME 'pwsh.exe') -Arguments @('-NoProfile', '-File', 'eng/scripts/Test-TemplatePackage.ps1', '-Live', '-CandidateFeed', $candidateFeed, '-CandidateVersion', $candidateVersion) -MinimumGiB 6
    Write-CandidateReceipt -Check template-consumer -Log template-consumer.log -Packages
    Get-Content -LiteralPath (Join-Path $candidateRoot 'combined-package-consumer.log'), (Join-Path $candidateRoot 'template-consumer.log') | Set-Content -LiteralPath (Join-Path $candidateRoot 'windows-consumer.log') -Encoding utf8
    Write-CandidateReceipt -Check windows-consumer -Log windows-consumer.log -Packages
} finally {
    if ($owned -and (Test-Path -LiteralPath (Join-Path $candidateRepo 'eng/.fixture.env'))) { & "$candidateRepo/eng/scripts/Stop-Neo4jFixture.ps1" }
    Pop-Location
}
