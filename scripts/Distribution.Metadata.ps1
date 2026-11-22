# Build a conservative dependency inventory from the production lock plus published
# runtime packs. This is metadata collection, not a native binary composition audit.
function Read-PackageXml([string] $Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader); return ,$xml }
    finally { $reader.Dispose() }
}

function Get-DistributionPackages([string] $RepositoryPath, [string] $PublishPath) {
    $lock = Get-Content -LiteralPath (Join-Path $RepositoryPath 'src/SnuWatcher.Desktop/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    $deps = Get-Content -LiteralPath (Join-Path $PublishPath 'SnuWatcher.Desktop.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $packages = @{}
    foreach ($target in $lock.dependencies.Values) {
        foreach ($name in $target.Keys) {
            $entry = $target[$name]
            if ($entry.type -eq 'Project') { continue }
            $key = $name + '/' + $entry.resolved
            $packages[$key] = @{ name = $name; version = $entry.resolved; contentHash = $entry.contentHash; scope = 'lockfile' }
        }
    }
    foreach ($key in $deps.libraries.Keys) {
        $entry = $deps.libraries[$key]
        if ($entry.type -notin 'package', 'runtimepack') { continue }
        $normalized = $key -replace '^runtimepack\.', ''
        if ($normalized -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*/[0-9][A-Za-z0-9_.+-]*\z') { throw 'Unsafe published dependency identity.' }
        if (!$packages.ContainsKey($normalized)) {
            $split = $normalized.Split('/')
            $packages[$normalized] = @{ name = $split[0]; version = $split[1]; contentHash = ''; scope = 'published-runtime' }
        } else { $packages[$normalized].scope = 'published-package' }
    }
    return @($packages.Values | Sort-Object name, version)
}

function Get-PackageLegalFiles([string] $PackagePath, [string] $RepositoryPath, [string] $Name, [Xml.XmlDocument] $Metadata) {
    $license = $Metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
    if ($license -and $license.GetAttribute('type') -eq 'file') {
        $licensePath = Assert-ReleaseChildPath $PackagePath (Join-Path $PackagePath $license.InnerText)
        if (!(Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw ('Declared license file missing for ' + $Name) }
    }
    $files = @(Get-ChildItem -LiteralPath $PackagePath -Recurse -File | Where-Object {
        $_.Name -match '^(?i:licen[cs]e|notice|third.?party.?notices|copying|copyright)(?:\.(?i:txt|md|rtf|html?))?$'
    } | ForEach-Object { @{ source = $_.FullName; path = [IO.Path]::GetRelativePath($PackagePath, $_.FullName).Replace('\', '/') } })
    if ($license -and $license.GetAttribute('type') -eq 'file' -and
        @($files | Where-Object { $_.source -eq $licensePath }).Count -eq 0) {
        $files += @{ source = $licensePath; path = $license.InnerText.Replace('\', '/') }
    }
    # Version-bound fallbacks require a fresh review when a dependency changes.
    $fallbacks = @{
        'AngleSharp/1.8.6' = 'AngleSharp-LICENSE.txt'; 'Microsoft.Data.Sqlite.Core/10.0.14' = 'Microsoft.Data.Sqlite-LICENSE.txt';
        'Tomlyn/2.10.1' = 'Tomlyn-LICENSE.txt'; 'Microsoft.Windows.SDK.NET.Ref/10.0.19041.57' = 'Windows-SDK-LICENSE.txt';
        'Microsoft.Windows.SDK.BuildTools/10.0.26100.4654' = 'Windows-SDK-LICENSE.txt'
    }
    $version = $Metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText
    $fallbackKey = $Name + '/' + $version
    if ($Name -in 'SQLitePCLRaw.bundle_e_sqlite3', 'SQLitePCLRaw.config.e_sqlite3', 'SQLitePCLRaw.core', 'SQLitePCLRaw.provider.e_sqlite3') {
        $fallbacks[$Name + '/3.0.5'] = 'SQLitePCLRaw-LICENSE.txt'
    }
    $hasLicenseText = ($license -and $license.GetAttribute('type') -eq 'file') -or
        @($files | Where-Object { [IO.Path]::GetFileName($_.path) -match '^(?i:licen[cs]e|copying)(?:\.(?i:txt|md|rtf|html?))?$' }).Count -gt 0
    if (!$hasLicenseText -or !$license) {
        if (!$fallbacks.ContainsKey($fallbackKey)) { throw ('No reviewed redistribution terms available for ' + $fallbackKey) }
        $source = Join-Path $RepositoryPath ('docs/third-party/' + $fallbacks[$fallbackKey])
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing reviewed license text for ' + $Name) }
        $files += @{ source = $source; path = $fallbacks[$fallbackKey] }
    }
    if ($files.Count -eq 0) { throw ('No redistribution terms available for ' + $Name) }
    return $files
}

function Write-DistributionMetadata([string] $RepositoryPath, [string] $PublishPath, $Configuration, $SourceIdentity) {
    $PublishPath = Assert-ReleaseChildPath $RepositoryPath $PublishPath
    $packages = @(Get-DistributionPackages $RepositoryPath $PublishPath)
    if ($packages.Count -eq 0) { throw 'The production dependency inventory is empty.' }
    $assets = Get-Content -LiteralPath (Join-Path $RepositoryPath 'src/SnuWatcher.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $legalRoot = Join-Path $PublishPath 'licenses'
    $components = @()
    $noticeParts = @('SNU Watcher ' + $Configuration.version,
        'Original SNU Watcher code is MIT licensed. Redistributed components retain their own terms below.',
        'Review and accept the applicable component terms when installing or redistributing this application.',
        (Get-Content -LiteralPath (Join-Path $RepositoryPath 'LICENSE') -Raw))
    # Validate all required terms before writing any apparent distribution metadata.
    $prepared = foreach ($package in $packages) {
        if ($package.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\z' -or $package.version -cnotmatch '^[0-9][A-Za-z0-9_.+-]*\z') { throw 'Unsafe dependency identity.' }
        $packagePath = $null
        foreach ($cacheRoot in $assets.packageFolders.Keys) {
            $candidate = Join-Path $cacheRoot ($package.name.ToLowerInvariant() + '/' + $package.version.ToLowerInvariant())
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
        }
        if (!$packagePath) { throw ('Restored dependency unavailable: ' + $package.name) }
        $nuspecs = @(Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File)
        if ($nuspecs.Count -ne 1) { throw ('Cannot identify dependency metadata: ' + $package.name) }
        $metadata = Read-PackageXml $nuspecs[0].FullName
        $id = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="id"]').InnerText
        $version = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText
        if ($id -ine $package.name -or $version -ine $package.version) { throw 'Package metadata identity mismatch.' }
        $legal = @(Get-PackageLegalFiles $packagePath $RepositoryPath $package.name $metadata)
        foreach ($file in $legal) {
            $legalBoundary = if ($file.source.StartsWith($packagePath, [StringComparison]::OrdinalIgnoreCase)) { $packagePath } else { $RepositoryPath }
            $null = Assert-ReleaseChildPath $legalBoundary $file.source
            if ([string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $file.source -Raw))) { throw 'An empty legal notice is not redistribution evidence.' }
        }
        @{ package = $package; root = $packagePath; metadata = $metadata; legal = $legal }
    }
    New-Item -ItemType Directory -Path $legalRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $RepositoryPath 'LICENSE') -Destination (Join-Path $PublishPath 'LICENSE')
    foreach ($item in $prepared) {
        $p = $item.package
        foreach ($file in $item.legal) {
            $relative = $p.name + '/' + $p.version + '/' + $file.path
            $destination = Assert-ReleaseChildPath $legalRoot (Join-Path $legalRoot $relative)
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.source -Destination $destination
            $noticeParts += ('===== ' + $relative + ' ====='), (Get-Content -LiteralPath $file.source -Raw)
        }
        $properties = @(@{ name = 'snu:inventory:scope'; value = $p.scope })
        if ($p.contentHash) { $properties += @{ name = 'snu:nuget:contentHash'; value = $p.contentHash } }
        $component = [ordered]@{ type = 'library'; 'bom-ref' = ('pkg:nuget/' + $p.name + '@' + $p.version);
            name = $p.name; version = $p.version; purl = ('pkg:nuget/' + $p.name + '@' + $p.version); properties = $properties }
        $archive = Join-Path $item.root ($p.name.ToLowerInvariant() + '.' + $p.version.ToLowerInvariant() + '.nupkg')
        if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { throw ('Missing actual NuGet archive: ' + $p.name) }
        $component.hashes = @(@{ alg = 'SHA-256'; content = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() })
        $license = $item.metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
        if ($license -and $license.GetAttribute('type') -eq 'expression') { $component.licenses = @(@{ expression = $license.InnerText }) }
        else { $component.licenses = @(@{ license = @{ name = ('See installed licenses/' + $p.name + '/' + $p.version) } }) }
        $components += $component
    }
    [IO.File]::WriteAllText((Join-Path $PublishPath 'DISTRIBUTION-LICENSES.txt'), ($noticeParts -join "`r`n`r`n"), [Text.UTF8Encoding]::new($false))
    $appRef = 'pkg:generic/DuckFard/SNU-Watcher@' + $Configuration.version
    $sbom = [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.6'; serialNumber = ('urn:uuid:' + [Guid]::NewGuid()); version = 1;
        metadata = @{ timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); component = @{ type = 'application';
            'bom-ref' = $appRef; name = 'SNU Watcher'; version = $Configuration.version; licenses = @(@{ expression = 'MIT' }) };
            properties = @(@{ name = 'snu:source:revision'; value = $SourceIdentity.revision },
                @{ name = 'snu:inventory:limitations'; value = 'Production NuGet lock plus published runtime packs. Includes restore/build packages; package archive hashes are not payload hashes. Embedded native third-party composition is not fully audited. See payload manifest and bundled notices.' }) };
        components = $components; dependencies = @(@{ ref = $appRef; dependsOn = @($components | ForEach-Object { $_.'bom-ref' }) });
        compositions = @(@{ aggregate = 'incomplete'; assemblies = @($appRef) }) }
    Write-ReleaseJson (Join-Path $PublishPath 'sbom.cdx.json') $sbom
}
