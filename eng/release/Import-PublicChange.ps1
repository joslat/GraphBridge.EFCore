#requires -Version 7.0
# Brings a change made in the public repository (a pull request, or a Dependabot fix or web edit merged into public
# main) into the private development repository, so the next snapshot publishes it instead of reverting it.
# Usage:
#   pwsh eng/release/Import-PublicChange.ps1 -PullRequest 12      # the pull request's changes, merged or not
#   pwsh eng/release/Import-PublicChange.ps1 -PublicMain          # commits made directly in public main
# The result is one commit on a new local branch (public-pr-12 or public-main-<sha>) based on private origin/main,
# crediting the original authors. It is built in a temporary worktree, so the current checkout and branch are untouched.
# Nothing is pushed. Review the branch, merge it into main, push, publish a snapshot, then close the pull request with a
# link to the snapshot commit. See eng/release/README.md.
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'PullRequest')] [int] $PullRequest,
    [Parameter(Mandatory, ParameterSetName = 'PublicMain')] [switch] $PublicMain,
    [string] $PublicUrl = 'https://github.com/joslat/GraphBridge.EFCore.git')
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot/../..")
git fetch origin main --quiet
if ($LASTEXITCODE) { throw 'Could not fetch the private origin.' }
git fetch $PublicUrl "+refs/heads/main:refs/public/main" --quiet
if ($LASTEXITCODE) { throw 'Could not fetch the public main branch.' }

if ($PullRequest) {
    git fetch $PublicUrl "+refs/pull/$PullRequest/head:refs/public/pr/$PullRequest" --quiet
    if ($LASTEXITCODE) { throw "Could not fetch pull request #$PullRequest." }
    $head = "refs/public/pr/$PullRequest"
    $base = (git merge-base refs/public/main $head).Trim()
    $branch = "public-pr-$PullRequest"
    $subject = "Apply public pull request #$PullRequest"
} else {
    # The newest public commit whose tree exists privately is the last snapshot; anything after it was made publicly.
    # Commits imported earlier carry a "Public-commit:" trailer and are skipped.
    $privateTrees = [Collections.Generic.HashSet[string]]::new([string[]] @(git log --format=%T origin/main))
    $base = @(git log --format='%H %T' refs/public/main) | Where-Object { $privateTrees.Contains($_.Split(' ')[1]) } | Select-Object -First 1
    if (-not $base) { throw 'No public commit matches a private tree; compare the two histories by hand.' }
    $base = $base.Split(' ')[0]
    $head = 'refs/public/main'
    $imported = @(git log --format='%(trailers:key=Public-commit,valueonly)' origin/main | Where-Object { $_ })
    $new = @(git log --no-merges --format=%H "$base..$head" | Where-Object { $_ -notin $imported })
    if (-not $new) { Write-Output 'Public main has no changes that are not already in the private history.'; exit 0 }
    # Import from the newest already-imported point, so earlier imports are not applied twice.
    $latest = git log --no-merges --format=%H "$base..$head" | Where-Object { $_ -in $imported } | Select-Object -First 1
    if ($latest) { $base = $latest }
    $branch = 'public-main-' + (git rev-parse --short $head).Trim()
    $subject = 'Apply changes made directly in the public repository'
}

$commits = @(git log --no-merges --format='- %h %s' "$base..$head")
if (-not $commits) { throw 'The change contains no commits.' }
$trailers = @(git log --no-merges --format='Public-commit: %H' "$base..$head")
$authors = @(git log --no-merges --format='%an <%ae>' "$base..$head" | Sort-Object -Unique)
$work = Join-Path ([IO.Path]::GetTempPath()) ('graphbridge-import-' + [Guid]::NewGuid().ToString('N'))
git diff --binary --output="$work.patch" $base $head
git worktree add --quiet -b $branch $work origin/main
if ($LASTEXITCODE) { throw "Could not create branch $branch; it may already exist." }
$committed = $false
try {
    git -C $work apply --3way --index "$work.patch"
    if ($LASTEXITCODE) { throw "The change does not apply cleanly to private main; nothing was committed. Apply $base..$head by hand." }
    # The Public-commit trailers let Publish-PublicSnapshot.ps1 see that these public commits are already included.
    $body = (@($commits) + '' + @($authors | ForEach-Object { "Co-authored-by: $_" }) + $trailers) -join "`n"
    git -C $work commit --quiet -m $subject -m $body
    if ($LASTEXITCODE) { throw 'Committing the imported change failed.' }
    $committed = $true
} finally {
    git worktree remove --force $work
    Remove-Item -Force "$work.patch" -ErrorAction SilentlyContinue
    if (-not $committed) { git branch -D $branch --quiet }
}
Write-Output "Imported $($commits.Count) public commit(s) as one commit on branch $branch, crediting: $($authors -join ', ')."
Write-Output "Next: review it, merge it into main, push, run Publish-PublicSnapshot.ps1, then close the public pull request with a link to the snapshot."
