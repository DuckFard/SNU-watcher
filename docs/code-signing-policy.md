# Code signing policy

**SNU Watcher's Windows releases are unsigned.** The project has no code signing certificate and uses no signing service. If that changes, the release notes will say so.

## What this means when you download

- Windows SmartScreen may warn that the installer comes from an unknown publisher. Before running it, compare its SHA-256 with `SHA256SUMS.txt` from the same release, as described in the [README](../README.md#download), and download only from this repository's [releases](https://github.com/DuckFard/SNU-watcher/releases).
- A checksum confirms the file matches what was published; it does not prove who published it.
- On a PC with Smart App Control turned on (Windows Security > App & browser control), Windows blocks unsigned apps and offers no "Run anyway", so SNU Watcher cannot start there.

## Checking a file

The installer and the files this project builds from its own source (`SnuWatcher.Desktop.exe` and the `SnuWatcher.*.dll` assemblies) carry no digital signature. If one of them lists a signer under **Properties > Digital Signatures**, it did not come from this project. Third-party components in the installer, such as the .NET runtime and Windows App SDK, keep their publishers' own signatures where they provide one; their terms are in `THIRD-PARTY-NOTICES.txt` with each release.

## Reporting a problem

If you find a file presented as an SNU Watcher release that this repository did not publish, report it as described in the [security policy](../SECURITY.md).
