#requires -Version 7.0
# Disposable end-to-end test of Publish-PublicSnapshot.ps1 and Import-PublicChange.ps1. Local bare repositories stand in
# for the private origin and the public GitHub repository; nothing real is pushed. Only the local ref refs/public/main is
# refreshed. Needs the gitleaks scanner at .artifacts/tools/gitleaks-8.30.1. Usage: pwsh eng/release/Test-PublicSnapshot.ps1
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path "$PSScriptRoot/../..").Path
$scanner = Join-Path $source '.artifacts/tools/gitleaks-8.30.1'
if (-not (Test-Path $scanner)) { throw "Install gitleaks 8.30.1 under $scanner first." }
git -C $source fetch --quiet --no-tags https://github.com/joslat/GraphBridge.EFCore.git '+refs/heads/main:refs/public/main'
if ($LASTEXITCODE) { throw 'Could not fetch the public main branch.' }
$t = Join-Path ([IO.Path]::GetTempPath()) ('gb-snapshot-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $t | Out-Null
$results = [Collections.Generic.List[string]]::new()
function Check($name, $condition) { $results.Add(($condition ? 'PASS ' : 'FAIL ') + $name) }
function Run($script, [string[]] $arguments) {
    $output = & pwsh -NoProfile -File (Join-Path $private "eng/release/$script") @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output }
}
try {
    git clone --quiet --bare --shared $source "$t/private.git"
    git init --quiet --bare --initial-branch=main "$t/public.git"
    git -C $source push --quiet "$t/public.git" refs/public/main:refs/heads/main
    git clone --quiet "$t/private.git" "$t/private"
    $private = "$t/private"
    Copy-Item "$source/eng/release/Publish-PublicSnapshot.ps1", "$source/eng/release/Import-PublicChange.ps1" "$private/eng/release/"
    $pub = "$t/public.git"

    $r = Run Publish-PublicSnapshot.ps1 @('-Status', '-PublicUrl', $pub)
    Check 'status reports the last snapshot' ($r.Exit -eq 0 -and $r.Output -match 'last snapshot')

    $r = Run Publish-PublicSnapshot.ps1 @('-Message', 'test', '-DryRun', '-PublicUrl', $pub)
    Check 'publish fails closed without the secret scanner' ($r.Exit -ne 0 -and $r.Output -match 'secret scanner is missing')

    New-Item -ItemType Directory "$private/.artifacts/tools" -Force | Out-Null
    Copy-Item -Recurse $scanner "$private/.artifacts/tools/"
    $r = Run Publish-PublicSnapshot.ps1 @('-Message', 'test', '-DryRun', '-PublicUrl', $pub)
    Check 'dry run scans and builds a snapshot' ($r.Exit -eq 0 -and $r.Output -match 'Dry run: not pushed' -and $r.Output -match 'no leaks found')

    # A contributor's pull request, never merged publicly.
    git clone --quiet $pub "$t/contrib"
    Add-Content "$t/contrib/CONTRIBUTING.md" "`nA contributor's line."
    git -C "$t/contrib" -c user.name='Ada Contributor' -c user.email='ada@example.com' commit --quiet -am 'Clarify contributing'
    git -C "$t/contrib" push --quiet origin HEAD:refs/pull/7/head
    $r = Run Import-PublicChange.ps1 @('-PullRequest', '7', '-PublicUrl', $pub)
    $message = git -C $private log -1 --format=%B public-pr-7 | Out-String
    Check 'pull request imported as one commit with credit and trailer' ($r.Exit -eq 0 -and $message -match 'Co-authored-by: Ada Contributor <ada@example.com>' -and $message -match 'Public-commit: [0-9a-f]{40}')
    Check 'pull request change is on the branch' ((git -C $private show public-pr-7:CONTRIBUTING.md | Out-String) -match "A contributor's line")
    Check 'checkout stays on main' ((git -C $private branch --show-current).Trim() -eq 'main' -and -not (git -C $private status --porcelain --untracked-files=no))

    # A direct edit merged into public main, while private main has moved on.
    git -C "$t/contrib" reset --quiet --hard origin/main
    Add-Content "$t/contrib/SECURITY.md" "`nA web edit."
    git -C "$t/contrib" -c user.name='Web Editor' -c user.email='web@example.com' commit --quiet -am 'Edit security policy on the web'
    git -C "$t/contrib" push --quiet origin HEAD:main
    $r = Run Publish-PublicSnapshot.ps1 @('-Status', '-PublicUrl', $pub)
    Check 'status flags the public-only commit' ($r.Exit -eq 1 -and $r.Output -match '1 not imported')
    $r = Run Publish-PublicSnapshot.ps1 @('-Message', 'test', '-DryRun', '-PublicUrl', $pub)
    Check 'publish refuses to revert the public change' ($r.Exit -ne 0 -and $r.Output -match 'would revert them')
    $r = Run Import-PublicChange.ps1 @('-PublicMain', '-PublicUrl', $pub)
    $branch = (git -C $private branch --list 'public-main-*' --format='%(refname:short)').Trim()
    Check 'public main change imported' ($r.Exit -eq 0 -and $branch -and ((git -C $private show "${branch}:SECURITY.md" | Out-String) -match 'A web edit'))
    git -C $private merge --quiet --no-edit $branch
    git -C $private push --quiet origin main
    $r = Run Publish-PublicSnapshot.ps1 @('-Status', '-PublicUrl', $pub)
    Check 'status is clean after the import' ($r.Exit -eq 0 -and $r.Output -match '0 not imported')
    $r = Run Publish-PublicSnapshot.ps1 @('-Message', 'Snapshot after import', '-PublicUrl', $pub)
    Check 'publish succeeds after the import' ($r.Exit -eq 0 -and $r.Output -match 'Published')
    $published = git -C $private show refs/public/main:SECURITY.md | Out-String
    Check 'published snapshot keeps the public edit' ($published -match 'A web edit')
    $r = Run Import-PublicChange.ps1 @('-PublicMain', '-PublicUrl', $pub)
    Check 'nothing left to import' ($r.Exit -eq 0 -and $r.Output -match 'no changes that are not already')
} finally {
    Set-Location ([IO.Path]::GetTempPath())
    Remove-Item -Recurse -Force $t -ErrorAction SilentlyContinue
}
$results
if ($results | Where-Object { $_.StartsWith('FAIL') }) { exit 1 }
