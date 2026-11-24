# Code signing policy

**Status: SNU Watcher's Windows releases are currently unsigned.** The project is applying to the free code signing program that [SignPath Foundation](https://signpath.org) offers open-source projects. Until a release's notes say it is signed, treat it as unsigned and follow the checksum steps in the [README](../README.md#download). This page describes how signing will work once the application is approved; the status line above changes only when a signed release is published.

Once approved, signed releases carry this attribution:

> Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

## Team and roles

- **Committers and reviewers:** [DuckFard](https://github.com/DuckFard), the maintainer. Changes from anyone else are merged only after the maintainer reviews them; see [the review of external contributions](decisions/0003-external-contribution-review.md) for an example.
- **Approvers:** [DuckFard](https://github.com/DuckFard). Every signing request is approved by hand, once per release.

Everyone in these roles must use multi-factor authentication for both GitHub and SignPath.

## What is signed

- The installer, `SnuCourseWatcher-<version>-windows-x64-Setup.exe`.
- The application files this project builds from its own source: `SnuWatcher.Desktop.exe` and the `SnuWatcher.*.dll` assemblies.

Third-party components in the installer (the .NET runtime, Windows App SDK, WebView2 and SQLite, among others) are included exactly as their publishers release them, with those publishers' own signatures where they provide one. They are not re-signed. Their terms are in `THIRD-PARTY-NOTICES.txt` with each release.

## How a signed release is made

1. The release candidate is built by this repository's GitHub Actions release workflow from a pushed, public commit. Builds from a developer's own computer are never submitted for signing.
2. SignPath.io verifies that the submitted files are the output of that automated build of this repository before signing them.
3. An approver approves the signing request by hand, only after the candidate has passed the qualification described in [preview releases](releases.md).
4. The signed files are checked again before publication, and the release notes state that the release is signed.

## Checking a signed file

Right-click the installer or `SnuWatcher.Desktop.exe`, choose **Properties**, then **Digital Signatures**. A signed SNU Watcher file lists **SignPath Foundation** as the signer, because the certificate is issued to SignPath Foundation, which vouches that the file was built from this repository. Windows SmartScreen also shows SignPath Foundation as the publisher. Any other signer, or a signature on a file this page says is unsigned, means the file did not come from this project.

## Privacy

SNU Watcher sends anonymous course-catalog, search and availability requests only to SNU's official registration service, for the searches and watches you set up. It has no project server, analytics or automatic crash reporting, and it does not upload your watches. The full statement is in [privacy and local data](privacy.md).

## Reporting misuse

If you find a file signed for SNU Watcher that this project did not release, or suspect the signing process was misused, report it as described in the [security policy](../SECURITY.md). You can also contact SignPath at support@signpath.io.
