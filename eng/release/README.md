# Releases and the public repository

GraphBridge is developed in a private repository, `joslat/GraphBridge.EFCore-dev`. The public repository, `joslat/GraphBridge.EFCore`, has a short history of its own: every public commit is one snapshot of a private commit's exact tree. Private history, branches and drafts never travel. The git-ignored `strategy/` folder is never part of a snapshot.

| Script | What it does |
|---|---|
| `Publish-PublicSnapshot.ps1 -Status` | Shows the last snapshot, private commits that aren't public yet, commits made directly in the public repository, and local commits not pushed. Changes nothing. |
| `Publish-PublicSnapshot.ps1 -Message "..." [-DryRun]` | Publishes HEAD's tree as one new public commit whose only parent is public main. |
| `Import-PublicChange.ps1 -PullRequest <n>` or `-PublicMain` | Brings a public pull request, or commits made directly in public main, into a new private branch as one commit that credits the authors. |
| `Test-PublicSnapshot.ps1` | Disposable end-to-end test of both scripts against local stand-in repositories. |
| `Check-PublicDocuments.py` | Checks that every relative link in the tracked Markdown files points at a tracked file, and never into `strategy/` or `.artifacts/`. |

## Publishing

1. Commit on private `main` and push it to `origin`.
2. Run `pwsh eng/release/Publish-PublicSnapshot.ps1 -Status` to see what will become public.
3. Run `pwsh eng/release/Publish-PublicSnapshot.ps1 -Message "One-line public summary"`.

Publish whenever a change should be visible, and always before a release: the release, live and docs workflows run in the public repository and see only what has been published.

The script refuses, and publishes nothing, when:

- tracked changes are uncommitted;
- HEAD isn't pushed to the private origin;
- public main has commits that aren't in the private history;
- the gitleaks scanner is missing from `.artifacts/tools/gitleaks-8.30.1`;
- the secret scan of the exact tree finds something.

## Pull requests and other public changes

Don't merge pull requests on GitHub, because a merged public commit blocks the next snapshot until it's imported. Instead:

1. Run `pwsh eng/release/Import-PublicChange.ps1 -PullRequest <n>`. It creates branch `public-pr-<n>` from private `origin/main` in a temporary worktree, so the current checkout isn't touched.
2. Review the branch, merge it into `main`, push, and publish a snapshot.
3. Close the pull request with a link to the snapshot commit. The contributor is credited with a `Co-authored-by` line.

If something did land directly in public main (a merged pull request, a Dependabot fix, a web edit), publishing refuses until you run `Import-PublicChange.ps1 -PublicMain`, then merge and push that branch. Imported commits carry `Public-commit:` trailers, which tell the publish script the public commits are already included.

## Releasing a version

A version is released when the build, the offline tests and the live tests pass on the exact commit being published. The three workflows below are manual, run in the public repository on fresh GitHub-hosted machines, and need no local database or memory.

1. Set `<Version>` in the published projects (five; the experimental GraphRAG projects ship with a later preview), add the version's section to `CHANGELOG.md`, then merge to private `main` and push.
2. Publish a snapshot (above). Every later step runs on that public commit.
3. Run **Release packages** (`release.yml`) with `version` set and `publish` off. It builds with locked dependencies, runs the unit, tooling and generator tests, checks the compiled query gallery, packs the five packages and checks each one's metadata, icon and source commit.
4. Run **Live Neo4j acceptance** (`live.yml`) with `ref` set to the public commit and `version` set to the newest version already on nuget.org. It runs every live suite against a disposable Neo4j: provider, selected EF Core specification tests, tooling and workshop. Its `nuget-org` job installs that published version from nuget.org. The published-template live check is skipped with a notice, because it compares packages with the files of the commit they were built from; it runs in step 7.
5. Run **Source and dependency security checks** (`security.yml`). It scans the history and tree for secrets, checks the dependency inventory and audits every package for known advisories.
6. When all three pass, run **Release packages** again with `publish` on and approve the `nuget` environment. That run builds and tests again, then pushes the packages it built.
7. Run **Live Neo4j acceptance** again with `version` set to the new version: it installs the template and every package from nuget.org and runs a project against Neo4j. You can start it right after publishing. Its `nuget-org` job first waits, for up to 30 minutes, until nuget.org search lists the version, because `dotnet new install` reads search, which lags package storage by about ten minutes.
8. Create the GitHub release on the public commit, for example `gh release create v<version> --target <public sha> --prerelease -R joslat/GraphBridge.EFCore`. This creates the public tag together with the release, on the public snapshot commit.
9. Tag the matching private commit with the same name, as an annotated tag (`git tag -a v<version> <private sha> -m "..."`), and push only that tag to `origin`. Each repository keeps its own release tag, as for 0.1.0-preview.2 and 0.1.0-preview.3: the public one on the snapshot, the private one on the commit it came from.

Fetch the public repository with `--no-tags`, as the scripts do, for example `git fetch --no-tags https://github.com/joslat/GraphBridge.EFCore.git "+refs/heads/main:refs/public/main"`. A plain fetch auto-follows the public release tag, and pushing tags afterwards would copy it into the private origin.

If a run fails, read its log and fix the cause in private `main` before running anything again. The fix needs a new snapshot, and steps 3 to 5 run again on it: never publish from a commit those checks did not pass on.

The hosted runs use Linux and Neo4j Community. When a change touches them, also run the [Enterprise permission suite](../enterprise/README.md) (evaluation license only) and the Neo4j 5.26 read probe (`eng/scripts/Run-CompatibilityProbe.ps1`) by hand.
