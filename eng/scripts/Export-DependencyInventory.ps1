#requires -Version 7.0
[CmdletBinding()]
param([switch] $Check)

$ErrorActionPreference = 'Stop'
$inventoryRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$inventoryDestination = Join-Path $inventoryRepo 'eng/dependencies/packages.json'
$inventoryAssets = Get-Content -Raw (Join-Path $inventoryRepo 'src/GraphBridge.EntityFrameworkCore/obj/project.assets.json') | ConvertFrom-Json -AsHashtable
$inventoryCaches = @($inventoryAssets.packageFolders.Keys)
$inventoryPackages = @{}
Push-Location $inventoryRepo
try {
    $inventoryLocks = @(git ls-files -- '**/packages.lock.json')
    if ($LASTEXITCODE -ne 0 -or $inventoryLocks.Count -eq 0) { throw 'Cannot enumerate tracked package locks.' }
    foreach ($lock in $inventoryLocks) {
        $data = Get-Content -Raw -LiteralPath $lock | ConvertFrom-Json -AsHashtable
        foreach ($framework in $data.dependencies.Values) {
            foreach ($name in $framework.Keys) {
                $dependency = $framework[$name]
                if ($dependency.type -eq 'Project') { continue }
                $key = ($name + '/' + $dependency.resolved).ToLowerInvariant()
                if (-not $inventoryPackages.ContainsKey($key)) {
                    $inventoryPackages[$key] = @{ Id = $name; Version = $dependency.resolved; Projects = [Collections.Generic.HashSet[string]]::new(); ContentHashes = [Collections.Generic.HashSet[string]]::new() }
                }
                $inventoryPackages[$key].Projects.Add($lock.Replace('/packages.lock.json', '')) | Out-Null
                $inventoryPackages[$key].ContentHashes.Add($dependency.contentHash) | Out-Null
            }
        }
    }
    $inventoryRows = foreach ($key in ($inventoryPackages.Keys | Sort-Object -CaseSensitive -Culture '')) {
        $item = $inventoryPackages[$key]
        if ($item.ContentHashes.Count -ne 1) { throw "Conflicting locked hashes for $key" }
        $folder = @($inventoryCaches | ForEach-Object { Join-Path $_ $key } | Where-Object { Test-Path -LiteralPath $_ })[0]
        if (-not $folder) { throw "Restore locked dependencies before exporting: $key is missing." }
        $nuspec = @(Get-ChildItem -LiteralPath $folder -Filter '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Expected one nuspec for $key" }
        $xml = [xml]::new()
        $xml.XmlResolver = $null
        $xml.Load($nuspec[0].FullName)
        $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        function Text([string] $name) {
            $node = $metadata.SelectSingleNode('*[local-name()="' + $name + '"]')
            if ($null -ne $node) { return $node.InnerText }
            return $null
        }
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $notices = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.Name -match 'license|notice|copyright' } | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        } | Sort-Object { $_.path } -CaseSensitive -Culture '')
        [ordered]@{
            id = $item.Id; version = $item.Version
            lockedContentHash = @($item.ContentHashes)[0]
            projects = @($item.Projects | Sort-Object -CaseSensitive -Culture '')
            licenseType = if ($license) { $license.GetAttribute('type') } else { $null }
            licenseValue = if ($license) { $license.InnerText } else { $null }
            licenseUrl = Text 'licenseUrl'; projectUrl = Text 'projectUrl'
            authors = Text 'authors'; copyright = Text 'copyright'
            noticeAssets = $notices
        }
    }
    $inventoryJson = ([ordered]@{ formatVersion = 1; scope = 'All tracked NuGet lock files; references, not bundled assemblies'; packages = @($inventoryRows) } | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"
    if ($Check) {
        if (-not (Test-Path $inventoryDestination) -or [IO.File]::ReadAllText($inventoryDestination).Replace("`r`n", "`n") -cne $inventoryJson) {
            throw 'Dependency inventory differs. Restore locked dependencies, regenerate, and review the changes.'
        }
    } else {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $inventoryDestination)) | Out-Null
        [IO.File]::WriteAllText($inventoryDestination, $inventoryJson, [Text.UTF8Encoding]::new($false))
    }
    Write-Output "Verified $($inventoryRows.Count) package/version entries from $($inventoryLocks.Count) tracked lock files."
} finally { Pop-Location }
