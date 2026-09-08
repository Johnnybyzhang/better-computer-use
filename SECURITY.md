# Security

Report suspected vulnerabilities through GitHub's private vulnerability reporting
feature when enabled for this repository. Do not post credentials, private captures
or a working exploit against another user's machine in a public issue. The
maintainer should enable private reporting before public launch.

Child sessions share the host account and filesystem. This project separates
interactive desktops; it is not a VM or an isolation boundary against malicious
code already running as that account or as administrator. Admin mode intentionally
runs the installed automation helper elevated following main-session UAC.

The internal OpenAI helper protocol is version-dependent. Only the current release
is maintained. Release checksums detect corruption; unsigned binaries are not a
publisher-authentication mechanism. See docs/validation.md for known limitations.
