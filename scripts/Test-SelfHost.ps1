param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.0-selfhost'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
$originalVersion = $env:GITVERSION_SEMVER
try {
    $env:GITVERSION_SEMVER = $Version
    $outputRoot = Join-Path $repositoryRoot 'artifacts'
    $bootstrap = Join-Path $outputRoot 'rx-bootstrap'
    $evidenceRoot = Join-Path $outputRoot 'selfhost'
    $resultPath = Join-Path $evidenceRoot 'release.json'
    $manifestPath = Join-Path $evidenceRoot 'release-manifest.json'
    $packageRoot = Join-Path $outputRoot 'packages'
    $package = Join-Path $packageRoot "Rexo.Cli.$Version.nupkg"
    # Remove only prior acceptance outputs so stale files cannot satisfy assertions.
    foreach ($path in @($resultPath, $manifestPath, $package)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $project = Join-Path $repositoryRoot 'src' 'Cli' 'Cli.csproj'
    & dotnet publish $project -c Release --output $bootstrap
    if ($LASTEXITCODE -ne 0) { throw 'Local CLI bootstrap failed.' }
    $cli = Join-Path $bootstrap 'Rexo.Cli.dll'
    & dotnet $cli --non-interactive check
    if ($LASTEXITCODE -ne 0) { throw 'Repository readiness check failed.' }
    $startedAt = [DateTime]::UtcNow
    & dotnet $cli --non-interactive --json-file $resultPath release
    if ($LASTEXITCODE -ne 0) { throw 'Self-hosted release failed.' }
    if (!(Test-Path -LiteralPath $resultPath) -or !(Test-Path -LiteralPath $manifestPath)) {
        throw 'Self-hosted release did not emit result and run manifest files.'
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (!$result.Success -or !$manifest.Success -or $result.ExitCode -ne 0 -or $manifest.ExitCode -ne 0) {
        throw 'Release output reports failure.'
    }
    if ($manifest.CommandExecuted -ne 'release' -or $manifest.Version.SemVer -ne $Version) {
        throw 'Release manifest has an unexpected command or version.'
    }
    if ($manifest.ConfigHash -notmatch '^[a-f0-9]{64}$') { throw 'Release config identity is missing.' }
    $artifact = @($manifest.Artifacts | Where-Object { $_.Name -eq 'Rexo.Cli' })
    if ($artifact.Count -ne 1 -or !$artifact[0].Built -or $artifact[0].Pushed) {
        throw 'Expected exactly one built, unpublished CLI artifact.'
    }
    if ([IO.Path]::GetFullPath($artifact[0].Location) -ne $packageRoot -or @($manifest.PushDecisions).Count -ne 0) {
        throw 'Release used an unexpected artifact location or attempted publication.'
    }
    if (!(Test-Path -LiteralPath $package)) { throw 'Expected CLI package is missing.' }
    $testRoot = Join-Path $outputRoot 'test-results'
    foreach ($filter in @('*.trx', 'coverage.cobertura.xml')) {
        $reports = @(Get-ChildItem -LiteralPath $testRoot -Recurse -Filter $filter |
            Where-Object { $_.LastWriteTimeUtc -ge $startedAt })
        if ($reports.Count -eq 0) { throw "Release did not produce fresh $filter evidence." }
    }
    $sarif = Join-Path $outputRoot 'sarif' 'dotnet-build.sarif'
    if (!(Test-Path -LiteralPath $sarif) -or (Get-Item -LiteralPath $sarif).LastWriteTimeUtc -lt $startedAt) {
        throw 'Release did not produce fresh SARIF evidence.'
    }
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $readme = $archive.GetEntry('PACKAGE_README.generated.md')
        if ($null -eq $readme) { throw 'Packaged README is missing.' }
        $reader = [IO.StreamReader]::new($readme.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($text -match '\{\{') { throw 'Packaged README contains unresolved template tokens.' }
    } finally { $archive.Dispose() }
    & dotnet $cli --non-interactive ci handoff
    if ($LASTEXITCODE -ne 0) { throw 'Verified package handoff failed.' }
    & dotnet $cli --non-interactive --dry-run ci publish --confirm
    if ($LASTEXITCODE -ne 0) { throw 'Verified package publication rehearsal failed.' }
    Write-Host "Self-host acceptance passed: $package; $manifestPath"
} finally {
    $env:GITVERSION_SEMVER = $originalVersion
    Pop-Location
}
