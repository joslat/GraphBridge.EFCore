#requires -Version 7.0
. "$PSScriptRoot/Candidate.Common.ps1"
Assert-CandidateSource
$linuxRoot = Join-Path $candidateRoot 'linux-consumer'
New-Item -ItemType Directory -Force -Path $linuxRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    [IO.File]::WriteAllText((Join-Path $linuxRoot $name), '<Project />', $utf8)
}
Copy-Item -LiteralPath (Join-Path $candidateRepo 'global.json') -Destination $linuxRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../consumers/CombinedPackageConsumer.cs') -Destination (Join-Path $linuxRoot 'Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../consumers/CombinedLiveProbe.cs') -Destination $linuxRoot
$references = @('GraphBridge.EntityFrameworkCore', 'GraphBridge.Tooling', 'GraphBridge.Generators') | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$candidateVersion]`" />" }
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup>' + ($references -join '') + '</ItemGroup><ItemGroup><Compile Remove="template/**/*.cs" /></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $linuxRoot 'CombinedConsumer.csproj'), $project, $utf8)
$config = '<configuration><packageSources><clear/><add key="candidate" value="/candidate"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="candidate"><package pattern="GraphBridge.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="/consumer/cache"/></config></configuration>'
[IO.File]::WriteAllText((Join-Path $linuxRoot 'NuGet.Config'), $config, $utf8)
$script = @'
set -euo pipefail
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet --info > environment.txt
sha256sum /candidate/*.nupkg > package-hashes.txt
dotnet restore CombinedConsumer.csproj --configfile NuGet.Config --disable-parallel -m:1 -nodeReuse:false
dotnet restore CombinedConsumer.csproj --configfile NuGet.Config --locked-mode --disable-parallel -m:1 -nodeReuse:false
dotnet build CombinedConsumer.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet bin/Release/net10.0/CombinedConsumer.dll | tee combined-result.json
dotnet new install /candidate/GraphBridge.Templates.VERSION.nupkg --debug:custom-hive /consumer/hive
dotnet new graphbridge --name LinuxVerifiedGraph --provider-version VERSION --output /consumer/template --debug:custom-hive /consumer/hive
dotnet restore template/LinuxVerifiedGraph.slnx --configfile /consumer/NuGet.Config --disable-parallel -m:1 -nodeReuse:false
dotnet restore template/LinuxVerifiedGraph.slnx --configfile /consumer/NuGet.Config --locked-mode --disable-parallel -m:1 -nodeReuse:false
dotnet build template/LinuxVerifiedGraph.slnx --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test template/Tests/LinuxVerifiedGraph.Tests.csproj --no-restore --no-build --logger 'console;verbosity=minimal'
test ! -f bin/Release/net10.0/GraphBridge.Generators.dll
echo 'Linux combined package runtime and installed-template build/two tests passed; no database or live claim.'
'@
[IO.File]::WriteAllText((Join-Path $linuxRoot 'verify.sh'), $script.Replace('VERSION', $candidateVersion).Replace("`r`n", "`n") + "`n", $utf8)
$name = 'graphbridge-candidate-linux-' + [Guid]::NewGuid().ToString('N')
$image = 'mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317'
try {
    Invoke-CandidateCommand -Log linux-consumer.log -Executable docker -Arguments @('run', '--rm', '--name', $name, '--label', "graphbridge.candidate=$candidateCommit", '--memory', '1536m', '--memory-swap', '1536m', '--cpus', '1', '--pids-limit', '192', '--mount', "type=bind,source=$candidateFeed,target=/candidate,readonly", '--mount', "type=bind,source=$linuxRoot,target=/consumer", '-w', '/consumer', $image, 'bash', '/consumer/verify.sh') -MinimumGiB 7
    $result = Get-Content -LiteralPath (Join-Path $linuxRoot 'combined-result.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'pass' -or $result.driverCalls -ne 0 -or $result.live) { throw 'Linux consumer did not confirm offline no-transport acceptance.' }
    foreach ($assembly in @($result.assemblies | Where-Object name -in @('GraphBridge.EntityFrameworkCore', 'GraphBridge.Tooling'))) {
        if ($assembly.productVersion.Split('+')[0] -ne $candidateVersion) { throw 'Linux loaded a different candidate.' }
    }
    Write-CandidateReceipt -Check linux-consumer -Log linux-consumer.log -Packages
} finally {
    $owned = & docker ps -a --filter "name=^/$name`$" --filter "label=graphbridge.candidate=$candidateCommit" --format '{{.Names}}'
    if ($owned -ceq $name) { & docker rm -f $name | Out-Null }
}
