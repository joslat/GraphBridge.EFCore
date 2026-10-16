#requires -Version 7.0
# Builds the documentation site from docs/ into .artifacts/docs-site with strict link and anchor checks.
# Uses a private Python environment under .artifacts/docs-venv with the pinned packages in eng/docs-site/requirements.txt.
# Pass -Serve to preview it at http://127.0.0.1:8000 instead. Nothing is published.
[CmdletBinding()]
param([switch] $Serve)
$ErrorActionPreference = 'Stop'
$docsRepo = (Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $docsRepo
$docsVenv = Join-Path $docsRepo '.artifacts/docs-venv'
$docsPython = if ($IsWindows) { Join-Path $docsVenv 'Scripts/python.exe' } else { Join-Path $docsVenv 'bin/python' }
if (-not (Test-Path -LiteralPath $docsPython)) {
    python -m venv $docsVenv
    if ($LASTEXITCODE) { throw 'Creating the documentation Python environment failed.' }
}
& $docsPython -m pip install --quiet --disable-pip-version-check -r eng/docs-site/requirements.txt
if ($LASTEXITCODE) { throw 'Installing the pinned documentation packages failed.' }
if ($Serve) {
    & $docsPython -m mkdocs serve --dev-addr 127.0.0.1:8000
} else {
    & $docsPython -m mkdocs build --clean
    if ($LASTEXITCODE) { throw 'Documentation site build failed; see the warnings above.' }
    Write-Output ('Documentation site: ' + (Join-Path $docsRepo '.artifacts/docs-site/index.html'))
}
