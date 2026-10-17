#requires -Version 7.0
# Publishes the current HEAD tree of the private development repository (joslat/GraphBridge.EFCore-dev) to the public
# repository (joslat/GraphBridge.EFCore) as ONE new commit on top of the public history. Private history never travels:
# the new commit's only parent is the current public main, and its tree is exactly HEAD's tree.
# Usage:
#   pwsh eng/release/Publish-PublicSnapshot.ps1 -Status                          # what the public repository is missing
#   pwsh eng/release/Publish-PublicSnapshot.ps1 -Message "Short public summary" [-DryRun]
# The whole procedure, including public pull requests, is in eng/release/README.md.
[CmdletBinding(DefaultParameterSetName = 'Publish')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Publish')] [string] $Message,
    [Parameter(ParameterSetName = 'Publish')] [switch] $DryRun,
    [Parameter(Mandatory, ParameterSetName = 'Status')] [switch] $Status,
    [string] $PublicUrl = 'https://github.com/joslat/GraphBridge.EFCore.git')
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot/../..")
git fetch origin main --quiet
if ($LASTEXITCODE) { throw 'Could not fetch the private origin.' }
git fetch $PublicUrl "+refs/heads/main:refs/public/main" --quiet
if ($LASTEXITCODE) { throw 'Could not fetch the public main branch.' }
$parent = (git rev-parse refs/public/main).Trim()

# The last snapshot is the newest public commit whose tree exists in the private history. Commits after it were made in
# the public repository (a merged pull request, a Dependabot fix, a web edit). Publishing over them would silently revert
# them, unless Import-PublicChange.ps1 already brought each one in, which it records as a "Public-commit:" trailer.
$privateCommits = @{}
foreach ($line in @(git log --format='%T %H' origin/main)) { $tree, $commit = $line.Split(' '); if (-not $privateCommits[$tree]) { $privateCommits[$tree] = $commit } }
$snapshot = @(git log --format='%H %T' refs/public/main) | Where-Object { $privateCommits.ContainsKey($_.Split(' ')[1]) } | Select-Object -First 1
if (-not $snapshot) { throw 'No public commit matches a private tree; compare the two histories by hand.' }
$snapshot, $snapshotTree = $snapshot.Split(' ')
$source = $privateCommits[$snapshotTree]
$imported = [Collections.Generic.HashSet[string]]::new([string[]] @(git log --format='%(trailers:key=Public-commit,valueonly)' origin/main | Where-Object { $_ }))
# Merge commits only combine commits that are checked individually.
$publicOnly = @(git log --no-merges --format='%H %h %s' "$snapshot..refs/public/main")
$missing = @($publicOnly | Where-Object { -not $imported.Contains($_.Split(' ')[0]) })

if ($Status) {
    Write-Output "Public main $($parent.Substring(0, 7)); last snapshot $($snapshot.Substring(0, 7)) of private $($source.Substring(0, 7))."
    if ($publicOnly.Count) { Write-Output "$($publicOnly.Count) commit(s) were made in the public repository, $($missing.Count) not imported yet:"; $publicOnly | ForEach-Object { '  ' + $_.Substring(41) } }
    $pending = @(git log --format='  %h %s' "$source..origin/main")
    if ($pending.Count) { Write-Output "$($pending.Count) private commit(s) are not public yet:"; $pending }
    else { Write-Output 'Every private commit on origin/main is public.' }
    $unpushed = @(git log --format='  %h %s' origin/main..HEAD)
    if ($unpushed.Count) { Write-Output "$($unpushed.Count) local commit(s) are not pushed to the private origin yet:"; $unpushed }
    if ($missing.Count) { Write-Output 'Import them before publishing: pwsh eng/release/Import-PublicChange.ps1 -PublicMain'; exit 1 }
    exit 0
}

if (git status --porcelain --untracked-files=no) { throw 'Commit or discard tracked changes first; the snapshot is HEAD.' }
$head = (git rev-parse HEAD).Trim()
if ((git rev-parse origin/main).Trim() -ne $head) { throw 'Push HEAD to the private origin first, so every public snapshot exists privately too.' }
$tree = (git rev-parse 'HEAD^{tree}').Trim()
if ((git rev-parse "$parent^{tree}").Trim() -eq $tree) { Write-Output 'Public main already has this exact tree; nothing to publish.'; exit 0 }
if ($missing.Count) {
    throw "Public main has $($missing.Count) commit(s) that are not in the private history; publishing now would revert them. Bring them in first: pwsh eng/release/Import-PublicChange.ps1 -PublicMain"
}

# Nothing is published without a secret scan of the exact tree.
$scanner = Join-Path '.artifacts/tools/gitleaks-8.30.1/bin' ($IsWindows ? 'gitleaks.exe' : 'gitleaks')
if (-not (Test-Path $scanner)) { throw "The secret scanner is missing at $scanner. Install gitleaks 8.30.1 there (see eng/release/README.md); nothing was published." }
$export = Join-Path ([IO.Path]::GetTempPath()) ('graphbridge-public-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $export | Out-Null
try {
    git archive --format=tar --output="$export.tar" HEAD
    # Windows' own tar reads drive-letter paths; Git's GNU tar mistakes "C:" for a remote host.
    & ($IsWindows ? (Join-Path $env:SystemRoot 'System32/tar.exe') : 'tar') -xf "$export.tar" -C $export
    if ($LASTEXITCODE) { throw 'Could not extract the snapshot for scanning; nothing was published.' }
    & $scanner dir $export --config=.gitleaks.toml --redact=100 --no-banner
    if ($LASTEXITCODE) { throw 'Secret scan of the snapshot found something; nothing was published.' }
} finally { Remove-Item -Recurse -Force $export, "$export.tar" -ErrorAction SilentlyContinue }

$sha = (git commit-tree $tree -p $parent -m $Message -m "Private development commit: $head").Trim()
Write-Output "Snapshot $sha (tree $tree, public parent $parent, private HEAD $head)"
if ($DryRun) { Write-Output 'Dry run: not pushed.'; exit 0 }
git push $PublicUrl "${sha}:refs/heads/main"
if ($LASTEXITCODE) { throw 'Push to the public repository failed.' }
git update-ref refs/public/main $sha
Write-Output "Published $sha to $PublicUrl main."
