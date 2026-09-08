# Release automation

The workflow runs on pull requests, pushes to `main`, `v*` tag pushes and manual
requests. Both Windows x64 and native Windows ARM64 must build successfully before
anything is published. ARM64 uses GitHub's `windows-11-arm` public-repository runner.

## Snapshots

Every successful push to `main` publishes an immutable GitHub prerelease named
`snapshot-<run-id>-<attempt>`. Manual runs on `main` do the same. Its package version
contains `snapshot.<run-number>.<attempt>`. Snapshots never become the latest stable
release. PRs and manual runs on other branches only produce workflow artifacts,
retained for 14 days. Snapshot prereleases remain until a maintainer deletes them.

## Versioned releases: tags only

1. Set `<Version>` in `src/BetterComputerUse/BetterComputerUse.csproj` to the intended
   SemVer version. Add `docs/release-notes/v<version>.md`; CI requires this file
   and publishes its content as the release notes. Update source plugin metadata.
2. Merge the reviewed change to `main` and let CI pass.
3. Push a matching tag, for example `v0.1.5`. The workflow rejects tags that do not
   exactly match the project's version. Pushing a version tag is the publication action.

Only a **tag push** creates a versioned release. Stable tags become the latest
release; SemVer prerelease tags remain prereleases. Build and validation finish for
both architectures before the publishing job receives `contents:write`. PR and
build jobs have read-only repository permissions. No personal token is needed.

Each release contains two self-contained ZIPs and SHA-256 sidecars. Each ZIP also
contains a full file manifest, architecture, source revision when built in CI,
plugin version, installer, MIT license, attribution and exact runtime notices.
OpenAI executables, local caches, reference checkouts and machine transcripts are
excluded. GitHub's release tag also supplies the corresponding source archive.

## Before making the repository public

Use `main` as the default branch, enable Actions and private vulnerability reporting,
and protect the default branch and version tags according to your maintainer access
policy. The Windows ARM runner requires an eligible repository/plan; the standard
public runner is the intended target. Publish a first snapshot and inspect both CI
jobs before tagging a versioned release. This local preparation has not executed
GitHub-hosted CI or created a remote repository.
