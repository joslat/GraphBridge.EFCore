# Explicit candidate qualification

These scripts build and inspect an explicitly selected clean source commit, package bytes and acceptance receipts. They do not publish packages or start GitHub Actions. Frozen historical candidate artifacts are not rewritten by selecting a new candidate.

In PowerShell 7, set `GRAPHBRIDGE_CANDIDATE_COMMIT` to the exact Git commit and `GRAPHBRIDGE_CANDIDATE_VERSION` to a new prerelease version before invoking a `Qualify-*.ps1` script. The selected commit must equal clean HEAD. Output is scoped to `.artifacts/release/candidate-<commit>-<version>`; never reuse a published version for different bytes.

- `Qualify-Offline.ps1`: locked solution build and offline suites.
- `Qualify-Light.ps1`: public links and source/history secret scans.
- `Qualify-Dependencies.ps1`: locked inventory and dependency audit.
- `Qualify-Live.ps1`, `Qualify-FreshSample.ps1`: explicitly prepared disposable-fixture and package/sample acceptance.
- `Qualify-Linux.ps1`: Docker-based isolated Linux consumer; inspect prerequisites and ownership policy before use.
- `Verify-CandidateMetadata.ps1`: package/source attribution and manifest validation.
- `Finalize-Candidate.ps1`: requires every named receipt and matching frozen package hashes.

Read each script's prerequisites. `Finalize-Candidate.ps1` lists verification sources with ripgrep, so `rg` must be on `PATH`. Security scanning expects an explicitly provisioned local scanner; Linux qualification expects Docker and its selected SDK image. These are release-maintainer workflows, not prerequisites for ordinary offline unit tests. Restore/build first, preserve shared RAM limits, and serialize heavyweight work. Historical receipt counts do not qualify a new candidate automatically.

Independent lightweight checks: `python eng/release/test_release_manifest.py` and `python eng/release/Check-PublicDocuments.py`.
