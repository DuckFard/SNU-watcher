# Shared offline packaging boundaries. These helpers never publish or modify Git.
function Write-ReleaseJson([string] $Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 60
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Test-ExactReleaseString($Value, [string] $Expected) {
    return $Value -is [string] -and [string]::Equals($Value, $Expected, [StringComparison]::Ordinal)
}

function Test-ReleaseInteger($Value) {
    return $Value -is [int] -or $Value -is [long]
}

function Get-ReleaseConfiguration([string] $RepositoryPath) {
    $value = Get-Content -LiteralPath (Join-Path $RepositoryPath 'release.json') -Raw | ConvertFrom-Json
    $number = '(?:0|[1-9][0-9]*)'
    $prerelease = '(?:[a-z][a-z0-9-]*|0|[1-9][0-9]*)'
    if (!(Test-ReleaseInteger $value.schemaVersion) -or $value.schemaVersion -ne 1 -or
        $value.version -isnot [string] -or $value.installerVersion -isnot [string] -or $value.runtimeFrameworkVersion -isnot [string] -or
        $value.version -notmatch "^$number\.$number\.$number(?:-$prerelease(?:\.$prerelease)*)?\z" -or
        $value.installerVersion -notmatch "^$number\.$number\.$number\.$number\z" -or
        $value.runtimeFrameworkVersion -notmatch "^$number\.$number\.$number\z") {
        throw 'Invalid release configuration; use a safe semantic version and numeric installer/runtime versions.'
    }
    if (@($value.installerVersion.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -gt 0) {
        throw 'Installer version components must fit Windows numeric version fields.'
    }
    if (($value.version -split '-')[0] -ne (($value.installerVersion.Split('.')[0..2]) -join '.')) {
        throw 'Installer and product version numbers must agree.'
    }
    $sdk = (Get-Content -LiteralPath (Join-Path $RepositoryPath 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($sdk -isnot [string] -or $sdk -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Invalid pinned SDK identity.' }
    return [pscustomobject][ordered]@{ schemaVersion = 1; version = $value.version; installerVersion = $value.installerVersion;
        runtimeFrameworkVersion = $value.runtimeFrameworkVersion; dotnetSdk = $sdk }
}

function Get-ReleaseVersionProperties($Configuration, [string] $Revision) {
    return @(('-p:Version=' + $Configuration.version), ('-p:AssemblyVersion=' + $Configuration.installerVersion),
        ('-p:FileVersion=' + $Configuration.installerVersion), ('-p:InformationalVersion=' + $Configuration.version + '+' + $Revision),
        '-p:ContinuousIntegrationBuild=true', '-p:IncludeSourceRevisionInInformationalVersion=false')
}

function Get-ReleaseBuildRecipe {
    return @('dotnet restore src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj --locked-mode -r win-x64 -p:SelfContained=true <version properties>',
        'dotnet publish src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o <isolated publish> <version properties>',
        'ISCC /DSourceDir=<isolated publish> /DOutputDir=<isolated installer> /DReleaseVersion=<version> /DInstallerVersion=<numeric version> installer/SnuWatcher.iss')
}

function Assert-ReleaseRuntimePacks([string] $PublishPath, [string] $ExpectedVersion) {
    # The exact SDK selects its bundled .NET patches. A global
    # RuntimeFrameworkVersion also overrides the unrelated Windows SDK pack.
    # Verify both actual .NET outputs instead, before packaging an installer.
    $deps = Get-Content -LiteralPath (Join-Path $PublishPath 'SnuWatcher.Desktop.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtime = Get-Content -LiteralPath (Join-Path $PublishPath 'SnuWatcher.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($deps.libraries -isnot [Collections.IDictionary]) { throw 'Published dependency metadata is invalid.' }
    $frameworks = @($runtime.runtimeOptions.includedFrameworks)
    $packs = [ordered]@{ 'Microsoft.NETCore.App' = 'Microsoft.NETCore.App.Runtime.win-x64';
        'Microsoft.WindowsDesktop.App' = 'Microsoft.WindowsDesktop.App.Runtime.win-x64' }
    foreach ($name in $packs.Keys) {
        $prefix = 'runtimepack.' + $packs[$name] + '/'
        $keys = @($deps.libraries.Keys | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
        if ($keys.Count -ne 1 -or !(Test-ExactReleaseString $keys[0] ($prefix + $ExpectedVersion)) -or
            !(Test-ExactReleaseString $deps.libraries[$keys[0]].type 'runtimepack')) {
            throw 'Published .NET runtime packs differ from release.json.'
        }
        $included = @($frameworks | Where-Object { Test-ExactReleaseString $_.name $name })
        if ($included.Count -ne 1 -or !(Test-ExactReleaseString $included[0].version $ExpectedVersion)) {
            throw 'Published .NET included frameworks differ from release.json.'
        }
    }
}

function Assert-PublicBuildFields($Value, $Configuration, [string] $Revision) {
    # Validate JSON value kinds before PowerShell comparisons: a Boolean true on
    # the left of -cne otherwise compares equal to every nonempty string.
    if (!(Test-ReleaseInteger $Value.schemaVersion) -or $Value.schemaVersion -ne 1 -or
        !(Test-ExactReleaseString $Value.source.revision $Revision) -or $Value.source.revision -cnotmatch '^[a-f0-9]{40}\z' -or
        $Value.source.dirty -isnot [bool] -or $Value.source.dirty) { throw 'Invalid exact release source identity.' }
    $strings = [ordered]@{ version = $Configuration.version; installerVersion = $Configuration.installerVersion;
        runtimeFrameworkVersion = $Configuration.runtimeFrameworkVersion; dotnetSdk = $Configuration.dotnetSdk;
        repository = 'https://github.com/DuckFard/SNU-watcher'; rid = 'win-x64'; configuration = 'Release'; signing = 'unsigned' }
    foreach ($field in $strings.Keys) {
        if (!(Test-ExactReleaseString $Value.$field $strings[$field])) { throw ('Invalid release identity string: ' + $field) }
    }
    $installerName = 'SnuCourseWatcher-' + $Configuration.version + '-windows-x64-Setup.exe'
    if (!(Test-ExactReleaseString $Value.installer.name $installerName) -or
        $Value.installer.sha256 -isnot [string] -or $Value.installer.sha256 -cnotmatch '^[a-f0-9]{64}\z') {
        throw 'Invalid exact installer identity.'
    }
    if ($Value.operatingSystem -isnot [string] -or $Value.innoSetup -isnot [string] -or
        ($Value.builtAtUtc -isnot [string] -and $Value.builtAtUtc -isnot [DateTime]) -or
        ($null -ne $Value.ciRunUrl -and $Value.ciRunUrl -isnot [string])) { throw 'Invalid public build field type.' }
    # PowerShell 7.5 can materialize ISO JSON strings as DateTime automatically.
    $timestamp = if ($Value.builtAtUtc -is [DateTime]) { $Value.builtAtUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } else { $Value.builtAtUtc }
    foreach ($field in 'version', 'installerVersion', 'runtimeFrameworkVersion') {
        if (!(Test-ExactReleaseString $Value.$field $Configuration.$field)) { throw 'Public build version identity differs from the configured preview.' }
    }
    if ($Value.operatingSystem -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z' -or
        $Value.innoSetup -cnotmatch '^6\.[0-9]+\.[0-9]+\z' -or $Value.dotnetSdk -cne $Configuration.dotnetSdk -or
        $timestamp -cnotmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z' -or
        ($null -ne $Value.ciRunUrl -and $Value.ciRunUrl -cnotmatch '^https://github\.com/DuckFard/SNU-watcher/actions/runs/[0-9]+\z')) {
        throw 'Public build fields contain unexpected data.'
    }
    $null = [DateTime]::ParseExact($timestamp, 'yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
    if ((@($Value.recipe) | ConvertTo-Json -Compress) -cne (@(Get-ReleaseBuildRecipe) | ConvertTo-Json -Compress) -or
        (@($Value.versionProperties) | ConvertTo-Json -Compress) -cne (@(Get-ReleaseVersionProperties $Configuration $Revision) | ConvertTo-Json -Compress)) {
        throw 'Public build recipe or version properties differ from the configured preview.'
    }
}

function Get-ReleaseSourceIdentity([string] $RepositoryPath, [string] $GitPath = 'git') {
    $revision = (& $GitPath -C $RepositoryPath rev-parse --verify HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '^[a-f0-9]{40}\z') { throw 'Cannot identify the source revision.' }
    $status = (& $GitPath -C $RepositoryPath status --porcelain --untracked-files=normal | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine whether source changes are committed.' }
    return [ordered]@{ revision = $revision; dirty = $status.Length -gt 0 }
}

function Assert-ReleaseChildPath([string] $Root, [string] $Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Packaging path escaped its intended root.' }
    # Reject junction/symlink traversal both at the leaf and along existing parents.
    $current = $absolute
    while ($current.Length -ge $prefix.TrimEnd('\', '/').Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Packaging does not follow symbolic links or junctions.'
            }
        }
        $current = Split-Path $current -Parent
        if (!$current) { break }
    }
    return $absolute
}

function Get-ReleaseInventory([string] $Root) {
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)) {
        $null = Assert-ReleaseChildPath $Root $file.FullName
        [ordered]@{ path = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/');
            bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Assert-ReleaseInventory([string] $Root, $Expected) {
    $actual = @(Get-ReleaseInventory $Root)
    if ($actual.Count -ne @($Expected).Count) { throw 'Payload file set changed after packaging.' }
    $names = @{}
    foreach ($item in @($Expected)) {
        if ($item.path -isnot [string] -or $item.sha256 -isnot [string] -or $item.sha256 -cnotmatch '^[a-f0-9]{64}\z' -or
            !(Test-ReleaseInteger $item.bytes) -or $item.bytes -lt 0 -or
            $item.path -notmatch '^[^:\\]+$' -or $item.path.StartsWith('/') -or
            @($item.path.Split('/') | Where-Object { $_ -in '.', '..', '' }).Count -gt 0 -or $names.ContainsKey($item.path)) {
            throw 'Unsafe or duplicate payload inventory path.'
        }
        $names[$item.path] = $true
        $match = @($actual | Where-Object { $_.path -ceq $item.path })
        if ($match.Count -ne 1 -or $match[0].bytes -ne $item.bytes -or $match[0].sha256 -cne $item.sha256) {
            throw 'Payload bytes changed after packaging.'
        }
    }
}

function Get-ReleaseInputInventory([string] $RepositoryPath) {
    $paths = @('release.json', 'global.json', 'installer/SnuWatcher.iss', 'LICENSE')
    $paths += @(Get-ChildItem -LiteralPath $RepositoryPath -File | Where-Object { $_.Name -like 'Directory.Build.*' } |
        Select-Object -ExpandProperty Name)
    $paths += @(Get-ChildItem -LiteralPath (Join-Path $RepositoryPath 'src') -Recurse -File |
        Where-Object { $_.Name -eq 'packages.lock.json' -or $_.Extension -eq '.csproj' } |
        ForEach-Object { [IO.Path]::GetRelativePath($RepositoryPath, $_.FullName).Replace('\', '/') })
    $paths += @(Get-ChildItem -LiteralPath (Join-Path $RepositoryPath 'scripts') -File -Filter '*.ps1' |
        ForEach-Object { 'scripts/' + $_.Name })
    $noticesPath = Join-Path $RepositoryPath 'docs/third-party'
    if (Test-Path -LiteralPath $noticesPath) {
        $paths += @(Get-ChildItem -LiteralPath $noticesPath -File |
            ForEach-Object { 'docs/third-party/' + $_.Name })
    }
    foreach ($path in @($paths | Sort-Object -Unique)) {
        $file = Join-Path $RepositoryPath $path
        [ordered]@{ path = $path; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Assert-ReleaseInputInventory([string] $RepositoryPath, $Expected) {
    $actual = @(Get-ReleaseInputInventory $RepositoryPath) | ConvertTo-Json -Depth 4 -Compress
    $recorded = @($Expected) | ConvertTo-Json -Depth 4 -Compress
    if ($actual -cne $recorded) { throw 'Packaging inputs differ from the recorded source build.' }
}

function Assert-ReleaseAssetSet([string] $AssetPath, $Configuration, [string] $ExpectedSourceRevision) {
    if ($ExpectedSourceRevision -cnotmatch '^[a-f0-9]{40}\z') { throw 'A full expected source revision is required.' }
    $expected = @('BUILD-PROVENANCE.json', 'PAYLOAD-MANIFEST.json', 'SHA256SUMS.txt', 'THIRD-PARTY-NOTICES.txt',
        'sbom.cdx.json', ('SnuCourseWatcher-' + $Configuration.version + '-windows-x64-Setup.exe')) | Sort-Object
    $files = @(Get-ReleaseInventory $AssetPath)
    if ((@($files.path | Sort-Object) -join "`n") -cne ($expected -join "`n")) { throw 'Unexpected or missing public release assets.' }
    $lines = @(Get-Content -LiteralPath (Join-Path $AssetPath 'SHA256SUMS.txt'))
    if ($lines.Count -ne $expected.Count - 1) { throw 'Checksum set must cover every other public asset.' }
    $seen = @{}
    foreach ($line in $lines) {
        if ($line -cnotmatch '^([a-f0-9]{64})  ([A-Za-z0-9._-]+)\z') { throw 'Invalid release checksum syntax.' }
        $hash = $Matches[1]; $name = $Matches[2]
        if ($name -ceq 'SHA256SUMS.txt' -or $name -cnotin $expected -or $seen.ContainsKey($name)) { throw 'Invalid or repeated checksum target.' }
        $seen[$name] = $true
        if ((Get-FileHash -LiteralPath (Join-Path $AssetPath $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'A public release asset failed its checksum.' }
    }
    $provenance = Get-Content -LiteralPath (Join-Path $AssetPath 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or $provenance.repository -cne 'https://github.com/DuckFard/SNU-watcher' -or
        $provenance.source.revision -cne $ExpectedSourceRevision -or $provenance.source.dirty -or
        $provenance.version -cne $Configuration.version -or $provenance.signing -cne 'unsigned' -or
        $provenance.rid -cne 'win-x64' -or $provenance.configuration -cne 'Release' -or
        !(Test-ExactReleaseString $provenance.claim 'Unsigned descriptive build metadata; not a signed attestation or byte-reproducibility guarantee.')) {
        throw 'Public release provenance differs from the expected preview.'
    }
    $installerName = 'SnuCourseWatcher-' + $Configuration.version + '-windows-x64-Setup.exe'
    if ($provenance.installer.name -cne $installerName -or $provenance.installer.sha256 -cne
        (Get-FileHash -LiteralPath (Join-Path $AssetPath $installerName) -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Public installer provenance differs from the downloaded bytes.'
    }
    Assert-PublicBuildFields $provenance $Configuration $ExpectedSourceRevision
}
