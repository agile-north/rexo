param(
    [string]$Configuration = "Release",
    [string]$PackageOutput = "artifacts/local-tool",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$toolId = "Rexo.Cli"

Push-Location $repoRoot
try {
    $packArgs = @(
        "pack",
        "src/Cli/Cli.csproj",
        "-c", $Configuration,
        "-o", $PackageOutput,
        "--nologo"
    )

    if ($NoBuild) {
        $packArgs += "--no-build"
    }

    Write-Host "Packing $toolId from source..."
    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed with exit code $LASTEXITCODE."
    }

    $packages = Get-ChildItem -Path $PackageOutput -Filter "Rexo.Cli.*.nupkg" |
        Where-Object { $_.Name -notlike "*.symbols.nupkg" } |
        Sort-Object LastWriteTimeUtc -Descending

    $latestPackage = $packages | Select-Object -First 1
    if (-not $latestPackage) {
        throw "No Rexo.Cli package was produced in '$PackageOutput'."
    }

    if ($latestPackage.Name -notmatch '^Rexo\.Cli\.(?<version>.+)\.nupkg$') {
        throw "Could not parse package version from '$($latestPackage.Name)'."
    }

    $version = $matches.version
    Write-Host "Using package: $($latestPackage.Name)"

    $installed = (dotnet tool list --global | Select-String -Pattern '^\s*rexo\.cli\s' -Quiet)

    if ($installed) {
        Write-Host "Updating global tool $toolId to version $version from local source..."
        & dotnet tool update --global $toolId --add-source $PackageOutput --version $version --ignore-failed-sources
    }
    else {
        Write-Host "Installing global tool $toolId version $version from local source..."
        & dotnet tool install --global $toolId --add-source $PackageOutput --version $version --ignore-failed-sources
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool install/update failed with exit code $LASTEXITCODE."
    }

    Write-Host "Done. You can now run 'rx --help' (or open a new shell if PATH needs refresh)."
}
finally {
    Pop-Location
}
