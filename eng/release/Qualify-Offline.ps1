#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Push-Location $candidateRepo
try {
    Assert-CandidateSource
    & dotnet --info | Set-Content -LiteralPath (Join-Path $candidateRoot 'environment.txt') -Encoding utf8
    Invoke-CandidateCommand -Log locked-build.log -Executable dotnet -Arguments @('restore', 'GraphBridge.sln', '--locked-mode', '--disable-parallel', '-m:1', '-nodeReuse:false')
    Invoke-CandidateCommand -Log locked-build.log -Executable dotnet -Arguments @('build', 'GraphBridge.sln', '--no-restore', '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false')
    Write-CandidateReceipt -Check locked-build -Log locked-build.log
    foreach ($suite in @(
        @{ Name = 'provider-unit'; Project = 'GraphBridge.UnitTests' },
        @{ Name = 'generator'; Project = 'GraphBridge.Generators.Tests' },
        @{ Name = 'tooling-offline'; Project = 'GraphBridge.ToolingTests' }
    )) {
        Invoke-CandidateCommand -Log ($suite.Name + '.log') -Executable dotnet -Arguments @('test', "tests/$($suite.Project)/$($suite.Project).csproj", '--no-build', '--no-restore', '--logger', 'console;verbosity=minimal', '--logger', "trx;LogFileName=$($suite.Name).trx", '--results-directory', $candidateRoot)
        if ($suite.Name -ne 'tooling-offline') { Write-CandidateReceipt -Check $suite.Name -Log ($suite.Name + '.log') }
    }
    Invoke-CandidateCommand -Log gallery.log -Executable dotnet -Arguments @('samples/05-FluentCypher/bin/Debug/net10.0/FluentCypher.Sample.dll', '--check')
    foreach ($project in @('src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj', 'src/GraphBridge.Tooling/GraphBridge.Tooling.csproj', 'src/GraphBridge.Generators/GraphBridge.Generators.csproj', 'templates/GraphBridge.Templates.csproj')) {
        Invoke-CandidateCommand -Log packages.log -Executable dotnet -Arguments @('pack', $project, '--no-restore', '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false', "-p:Version=$candidateVersion", "-p:PackageVersion=$candidateVersion", "-p:RepositoryCommit=$candidateCommit", '-o', $candidateFeed)
    }
    Get-ChildItem -LiteralPath $candidateFeed -Filter '*.nupkg' | ForEach-Object {
        [ordered]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $candidateRoot 'package-hashes.json') -Encoding utf8
} finally { Pop-Location }
