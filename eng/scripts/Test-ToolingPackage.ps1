#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$toolingRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-PackageMetadata.ps1')
$toolingVersion = '0.1.0-packagecheck.' + [Guid]::NewGuid().ToString('N')
$toolingOutput = Join-Path $toolingRepo ('.artifacts/tooling-package/' + [Guid]::NewGuid().ToString('N'))
Push-Location $toolingRepo
try {
    dotnet pack src/GraphBridge.Tooling/GraphBridge.Tooling.csproj --no-restore -m:1 -p:UseSharedCompilation=false "-p:PackageVersion=$toolingVersion" -o $toolingOutput
    if ($LASTEXITCODE) { throw 'Tooling package build failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $toolingOutput "GraphBridge.Tooling.$toolingVersion.nupkg"))
    try {
        $reader = [IO.StreamReader]::new($archive.GetEntry('GraphBridge.Tooling.nuspec').Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert-GraphBridgePackageMetadata -Archive $archive -Manifest $manifest -RepositoryRoot $toolingRepo
        $dlls = @($archive.Entries | Where-Object { $_.FullName -like '*.dll' } | ForEach-Object { $_.FullName })
        if ($dlls.Count -ne 1 -or $dlls[0] -cne 'lib/net10.0/GraphBridge.Tooling.dll') { throw 'Tooling package unexpectedly bundles other assemblies.' }
        if ($null -eq $archive.GetEntry('README.md')) { throw 'Tooling package README missing.' }
        $dependencyIds = @($manifest.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { $_.GetAttribute('id') } | Sort-Object)
        $expectedIds = @('GraphBridge.EntityFrameworkCore', 'AngleSharp', 'dotNetRdf.Core', 'dotNetRdf.Shacl', 'Microsoft.CodeAnalysis.CSharp', 'YamlDotNet') | Sort-Object
        if (@(Compare-Object $expectedIds $dependencyIds -CaseSensitive).Count -ne 0) { throw 'Tooling package dependency boundary changed.' }
    } finally { $archive.Dispose() }
    Write-Output 'Optional tooling package payload and dependency references verified; no compiled CLI bundle or publication claimed.'
} finally { Pop-Location }
