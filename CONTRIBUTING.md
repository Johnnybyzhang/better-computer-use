# Contributing

Use Windows and the .NET 10 SDK. Run `./scripts/build.ps1 -Test` from a standard
interactive user session. `-Ci` runs the hosted-runner subset and reports skipped
interactive checks. Native RDP/UAC testing must run on a disposable child desktop;
never log off someone else's active task to make a test pass.

Keep changes focused and include the validation performed and its limits. Update
the skill when tool behavior changes. Do not commit installed OpenAI binaries,
reference checkouts, local runtime caches, captures, credentials or chat transcripts.

Build distributable bundles with `scripts/package.ps1 -Runtime win-x64` or
`-Runtime win-arm64`. See [release automation](docs/releases.md). Changes to CI,
IPC, elevation, session ownership and installer paths need particular care.
Contributions are provided under the repository's MIT license.
