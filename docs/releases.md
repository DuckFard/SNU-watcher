# Unsigned Windows preview releases

The owner authorized an **unsigned preview after all functional gates pass**. The first target is `0.1.0-preview.1`, tag `v0.1.0-preview.1`, with Windows numeric installer version `0.1.0.0`. This document is a procedure, not evidence that a release has passed or been published. Read the current [progress](PROGRESS.md) and candidate-specific native acceptance before publishing.

Original project code remains MIT licensed. Microsoft Windows App SDK, Windows SDK, WebView2, .NET, SQLite and other components retain their own applicable terms. The installer presents `DISTRIBUTION-LICENSES.txt`, containing the original project license and exact collected upstream terms/notices. Installed `licenses/` preserves the individual files. This collection is not a new project EULA and does not relabel third-party code as MIT.

This preview has no Authenticode publisher signature. Windows may show an unknown-publisher or reputation warning. SHA-256 checksums detect changed downloads; they do not establish publisher identity. Do not disable Windows security features. A future signing decision is separate from the authorized preview; it must not be silently described as already signed.

## Build identity and package contents

`release.json` is the canonical product/numeric/runtime version input. `Build-Native.ps1` verifies the exact SDK in `global.json`, restores the production project in locked mode for `win-x64`, and publishes self-contained .NET/Windows App SDK files into a new `artifacts/native/<GUID>` directory. The exact SDK selects its bundled .NET patches; before compiling an installer, the script requires both published Core and Windows Desktop runtime-pack identities and included-framework versions to match `release.json`. It does not set a global `RuntimeFrameworkVersion`, which would incorrectly override the unrelated Windows SDK framework version. Version properties apply to the assemblies and installer. The stable AppId, AUMID, publisher and per-user installation path are retained for upgrades.

Canonical restore/publish properties include `ContinuousIntegrationBuild=true` so the SDK maps developer source/PDB paths. `IncludeSourceRevisionInInformationalVersion=false` keeps the explicit product version's single source SHA; the SDK must not append it a second time. Qualification checks the actual five application DLLs and five portable PDBs for developer username, home and workspace paths, and inspects their CodeView, document and SourceLink paths and exact product versions. Passing command-line fixtures does not replace this binary check. SourceLink URLs must identify the accepted final source revision; a successful private proof against an earlier commit does not qualify a later release.

The build records source revision/dirty status, input hashes, SDK/runtime/compiler versions, OS version, build recipe, optional verified GitHub run URL, complete payload file hashes and final installer hash. Existing candidates are preserved. Developer builds may record uncommitted changes. Release staging requires the same **clean committed source** and unchanged recorded inputs/payload/installer.

This is a repeatable recipe with pinned SDK/runtime/NuGet inputs and a recorded Inno compiler. It does not promise byte-identical installers across different Windows/compiler environments. Updating a bundled runtime requires a new build, qualification and release.

The mandatory public asset set is exactly:

| Asset | Purpose |
| --- | --- |
| `SnuCourseWatcher-0.1.0-preview.1-windows-x64-Setup.exe` | Self-contained per-user Windows 11 x64 installer |
| `SHA256SUMS.txt` | Lowercase SHA-256 for every other asset |
| `THIRD-PARTY-NOTICES.txt` | The full distribution license/notice text shown by the installer |
| `sbom.cdx.json` | CycloneDX 1.6 dependency inventory |
| `PAYLOAD-MANIFEST.json` | Relative paths, sizes and SHA-256 for every installed payload file |
| `BUILD-PROVENANCE.json` | Unsigned descriptive source/toolchain/build identity |

The SBOM covers the entry application's production NuGet lock graph plus runtime packs found in its published `.deps.json`; it includes some build/restore-only components. It explicitly declares **incomplete** composition because the embedded native components of vendor binaries have not received a complete independent audit. Bundled vendor notices remain necessary. Package hashes are SHA-256 of actual `.nupkg` files. NuGet `contentHash` is retained as named metadata, not mislabeled as a raw archive checksum. Payload hashes are in the separate manifest.

No profile database, course selections, raw diagnostics, host environment variables or arbitrary local paths are release assets. The scripts never upload application data.

## Local preparation

Run from the repository in PowerShell 7. The commands below build files; they do not install, tag, push or publish. Use the exact SDK in `global.json` and a verified Inno Setup 6 compiler. Do not run concurrent builds against this checkout.

```powershell
./tests/scripts/Test-NativeBuildOutput.ps1
./tests/scripts/Test-ReleaseAssets.ps1
./scripts/Test-Foundation.ps1
./scripts/Build-Native.ps1 -DotNetPath dotnet -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

The build prints `Build output root: <path>`. After the source changes are committed, build again from the clean revision intended for release, complete all offline/live/native/upgrade/repair/uninstall gates against these exact bytes, and stage that exact build:

```powershell
$buildRoot = 'C:\absolute\path\to\repository\artifacts\native\<GUID>'
./scripts/New-ReleaseAssets.ps1 -BuildRoot $buildRoot
$sourceRevision = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
$assetPath = Join-Path $buildRoot 'release'
./scripts/Confirm-ReleaseAssets.ps1 -AssetPath $assetPath -ExpectedSourceRevision $sourceRevision
```

Do not edit the staged assets or reuse their directory for a replacement. Correct the source, build a new isolated candidate, and repeat its qualification. `New-ReleaseAssets.ps1` rejects stale source, dirty source, missing notices, tampered files and an existing release destination. A failed build or staging attempt is retained for diagnosis.

`.github/workflows/release-candidate.yml` provides a manual `workflow_dispatch` alternative. It runs offline tests and creates the same six assets on `windows-2025`, with `contents: read` only. Its uploaded artifact is a candidate, not native acceptance or a public release. Downloading its assets and later uploading them elsewhere does not turn a local build into an Actions-built artifact.

## Publish the approved exact candidate

Only the owner-controlled release step follows after all gates pass. The GitHub connector currently lacks release-write tools, so use the already authenticated GitHub CLI. Do not print a token or change credentials. Prepare reviewed `release-notes.md` in an ignored local work directory, including:

- Unsigned preview status, Windows 11 x64 support and the exact accepted source commit/installer SHA-256.
- Verified functionality, remaining preview limitations and the test evidence links.
- Manual registration, monitoring policy, and the fact that alerts do not reserve a seat.
- Checksum verification, component terms, manual upgrade and rollback instructions.

Tag the already accepted, pushed source revision; verify its remote tag resolves to that exact commit before creating a release. Do not move/reuse an existing release tag. The following draft commands are for the final authorized release operation, not routine CI:

```powershell
$tag = 'v0.1.0-preview.1'
$repo = 'DuckFard/SNU-watcher'
# $sourceRevision is the full approved commit, not an unreviewed current branch.
git tag -a $tag $sourceRevision -m 'SNU Watcher 0.1.0-preview.1 unsigned preview'
if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
git push origin $tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push failed.' }
gh api "repos/$repo/git/ref/tags/$tag"
if ($LASTEXITCODE -ne 0) { throw 'Remote tag lookup failed.' }
# For an annotated tag, inspect its returned tag object and confirm the peeled
# commit is $sourceRevision before continuing.
gh release create $tag --repo $repo --verify-tag --draft --prerelease --latest=false --title 'SNU Watcher 0.1.0-preview.1' --notes-file 'C:\absolute\path\to\release-notes.md'
if ($LASTEXITCODE -ne 0) { throw 'Draft creation failed.' }
$assets = @(Get-ChildItem -LiteralPath $assetPath -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
gh release upload $tag @assets --repo $repo
if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; inspect the draft before retrying.' }
```

Never use `--clobber`: it replaces existing assets. Inspect a partial draft explicitly rather than silently replacing any bytes. Download into a new directory and verify the closed set against the approved source:

```powershell
$downloadPath = Join-Path $buildRoot ('release-download-' + [Guid]::NewGuid().ToString('N'))
gh release download $tag --repo $repo --dir $downloadPath
if ($LASTEXITCODE -ne 0) { throw 'Release download failed.' }
./scripts/Confirm-ReleaseAssets.ps1 -AssetPath $downloadPath -ExpectedSourceRevision $sourceRevision
# Compare the downloaded installer SHA-256 to the independently recorded native
# acceptance SHA-256 before publishing the reviewed draft.
gh release edit $tag --repo $repo --draft=false --prerelease --latest=false
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
```

Repeat the download/hash check from the public release and verify the public installer download/install path in a clean interactive Windows system. Auto-generated source archives are not part of the six manually uploaded assets.

GitHub build attestations are free for public repositories, but optional here. They require the workflow that builds the exact bytes to create an attestation. A local provenance JSON is descriptive metadata, not a signed build attestation. GitHub immutable-release attestations, when available/enabled, bind a release's tag/commit/assets; they are separate from build provenance and Windows Authenticode signing. Do not change repository immutability, permissions, credentials or signing services as a side effect of this procedure.

## Manual upgrade and rollback

Download a newer qualified installer from this repository's Releases page, verify its checksums/source identity, quit SNU Watcher, and install in the same per-user location. There is no automatic updater. Preserve the previous known-good installer and its acceptance evidence. A repair must use the exact supported installer and preserve user data.

If a preview is faulty, mark the release clearly as withdrawn and link the known-good release. Stop advertising the faulty download; do not rewrite its tag, checksum or binary. If removal is necessary, make that explicit in release notes and retain internal acceptance evidence. A replacement gets a new version/tag.

Quit the app before any local data backup/restore. Follow the [migration and data compatibility guidance](migration.md); do not open a database with an older app unless its schema compatibility is verified. A raw local backup may contain private watch data and must stay on the student's device. Diagnostic export is not a database backup. Reinstalling an older program does not automatically roll back database migrations. If no safe earlier app/data pair exists, stop watching with the faulty app and use official SNU registration while the fix is qualified.

## Primary references

- [GitHub CLI create](https://cli.github.com/manual/gh_release_create), [upload](https://cli.github.com/manual/gh_release_upload), [download](https://cli.github.com/manual/gh_release_download), [edit](https://cli.github.com/manual/gh_release_edit).
- [Artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) and [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).
- [CycloneDX 1.6 JSON](https://cyclonedx.org/docs/1.6/json/), [NuGet license metadata](https://learn.microsoft.com/en-us/nuget/reference/nuspec), and [NuGet signature-independent content hash](https://github.com/NuGet/Home/issues/10787).
- [Exact self-contained runtime version](https://learn.microsoft.com/en-us/dotnet/core/versions/selection) and [Inno numeric version](https://jrsoftware.org/ishelp/topic_setup_versioninfoversion.htm)/[text product version](https://jrsoftware.org/ishelp/topic_setup_versioninfoproducttextversion.htm).
- [Additional vendored license sources and hashes](third-party/release-license-sources.json); package-supplied license/notice files come from the actual restored versions.
