# Security policy

The first public distribution is an unsigned Windows 11 x64 preview. It is not an Authenticode-signed production release. Obtain it only from the project's [GitHub Releases](https://github.com/DuckFard/SNU-watcher/releases), compare the downloaded installer with the published SHA-256 checksum, and read the release's limitations. Checksums establish byte identity with those published assets; they do not establish Authenticode identity or guarantee safety.

SNU Watcher does not accept SNU credentials or automate registration. The official registration and Genie destinations are fixed HTTPS links. Do not supply credentials, registration tokens, authenticated cookies or arbitrary URLs through issues or diagnostics.

## Reporting a vulnerability

Use the repository's private **Report a vulnerability** option when available. If private reporting is unavailable, open an issue requesting a private contact channel without exploit details or sensitive data. Do not publish personal databases, session tokens, credentials or an exploit that endangers other students.

Include the app version, Windows version, a concise description, expected impact and safe reproduction steps using fixture data. A diagnostic export is optional; inspect it before sharing. This project does not promise a fixed response time or a bounty.

## Maintainer release checks

- Keep dependency locks and native SQLite identity checks intact; audit transitive dependencies before a release.
- Review anonymous source requests, input validation, fixed browser destinations, exported diagnostic fields and update/installer behavior.
- Require the offline tests, coverage gates, installed notification checks, clean Windows verification and current live-source recheck for the exact candidate.
- Publish only the reviewed installer and its checksums, required notices, scoped SBOM and build record. Never publish user profiles, databases, credentials, raw source responses or validation secrets.
- Preserve the stable per-user installation and notification identity. Require graceful shutdown and verified migration backups for upgrades. Stop distribution of a defective candidate and describe recovery limits rather than advising an unsafe database downgrade.
