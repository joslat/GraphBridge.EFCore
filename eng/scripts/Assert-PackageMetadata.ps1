# Shared package acceptance; dot-source after loading System.IO.Compression.FileSystem.
function Assert-GraphBridgePackageMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.IO.Compression.ZipArchive] $Archive,
        [Parameter(Mandatory)] [xml] $Manifest,
        [Parameter(Mandatory)] [string] $RepositoryRoot
    )
    $metadata = $Manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $license = $metadata.SelectSingleNode("*[local-name()='license']")
    if ($null -eq $license -or $license.GetAttribute('type') -cne 'expression' -or $license.InnerText -cne 'MIT') {
        throw 'Package must declare the MIT SPDX expression.'
    }
    foreach ($field in @('description', 'authors', 'projectUrl', 'tags', 'copyright')) {
        $node = $metadata.SelectSingleNode("*[local-name()='$field']")
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) { throw "Package metadata is missing $field." }
    }
    if ($metadata.SelectSingleNode("*[local-name()='authors']").InnerText -cne 'GraphBridge contributors') {
        throw 'Package contributor identity differs from reviewed metadata.'
    }
    $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
    if ($null -eq $repository -or $repository.GetAttribute('type') -cne 'git' -or
        $repository.GetAttribute('url') -cne 'https://github.com/joslat/GraphBridge.EFCore') {
        throw 'Package repository metadata differs from the reviewed source repository.'
    }
    $required = [ordered]@{
        'LICENSE' = 'LICENSE'
        'THIRD_PARTY_NOTICES.md' = 'eng/PACKAGE_NOTICES.md'
        'DEPENDENCY-INVENTORY.json' = 'eng/dependencies/packages.json'
    }
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'eng/dependencies/licenses') -File -Filter '*.txt') {
        $required['notices/' + $source.Name] = 'eng/dependencies/licenses/' + $source.Name
    }
    foreach ($entryName in $required.Keys) {
        $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $entryName })
        if ($entries.Count -ne 1) { throw "Package must contain exactly one $entryName." }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $actual = $reader.ReadToEnd().Replace("`r`n", "`n") } finally { $reader.Dispose() }
        $expected = [IO.File]::ReadAllText((Join-Path $RepositoryRoot $required[$entryName])).Replace("`r`n", "`n")
        if ($actual -cne $expected) { throw "Package attribution payload differs from reviewed source: $entryName" }
    }
    Write-Output "MIT metadata, repository identity and $($required.Count) license/notice/inventory payloads verified."
}
