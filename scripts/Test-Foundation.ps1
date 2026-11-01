param(
    [string]$Dotnet = 'dotnet',
    [switch]$SkipCoverageGate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    & $Dotnet restore SnuWatcher.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & $Dotnet build SnuWatcher.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Foundation build failed.' }

    $testTargets = @(
        @{ Project = 'SnuWatcher.Core.Tests'; Assembly = 'SnuWatcher.Core' },
        @{ Project = 'SnuWatcher.Integration.Tests'; Assembly = 'SnuWatcher.Infrastructure' },
        @{ Project = 'SnuWatcher.Windows.Tests'; Assembly = 'SnuWatcher.Windows' }
    )
    foreach ($target in $testTargets) {
        $testName = $target.Project
        # A unique results directory prevents stale coverage from masking a failed run.
        $resultsPath = Join-Path $repoRoot ('artifacts/test-results/' + $testName + '/' + [Guid]::NewGuid().ToString('N'))
        & $Dotnet test "tests/$testName/$testName.csproj" --configuration Release --no-build --settings tests/coverage.runsettings --collect 'XPlat Code Coverage' --logger trx --results-directory $resultsPath
        if ($LASTEXITCODE -ne 0) { throw "$testName failed." }

        $coverageFiles = @(Get-ChildItem -LiteralPath $resultsPath -Recurse -File -Filter coverage.cobertura.xml)
        if ($coverageFiles.Count -eq 0) { throw "No coverage report was generated for $testName." }
        # The TRX logger may copy the collector attachment into a second directory.
        # Accept identical copies only; never choose between conflicting reports.
        $coverageHashes = @($coverageFiles | Get-FileHash -Algorithm SHA256 | Select-Object -ExpandProperty Hash -Unique)
        if ($coverageHashes.Count -ne 1) { throw "Conflicting coverage reports were generated for $testName." }
        [xml]$coverageReport = Get-Content -Raw -LiteralPath $coverageFiles[0].FullName
        $culture = [Globalization.CultureInfo]::InvariantCulture
        # A dependency's coverage must never mask gaps in the assembly under test.
        $assemblies = @($coverageReport.SelectNodes('/coverage/packages/package') | Where-Object { $_.GetAttribute('name') -eq $target.Assembly })
        if ($assemblies.Count -ne 1) { throw "Expected one coverage entry for $($target.Assembly)." }
        $lineRate = [double]::Parse($assemblies[0].GetAttribute('line-rate'), $culture)
        $branchRate = [double]::Parse($assemblies[0].GetAttribute('branch-rate'), $culture)
        Write-Host ("{0}: lines {1:P1}, branches {2:P1}" -f $target.Assembly, $lineRate, $branchRate)
        if (-not $SkipCoverageGate -and ($lineRate -lt 0.8 -or $branchRate -lt 0.8)) {
            throw "$testName is below the 80% line/branch foundation coverage gate."
        }
    }
}
finally {
    Pop-Location
}
