<#
.SYNOPSIS
    Runs the tests, then builds self-contained release archives of Boot Video Manager.

.DESCRIPTION
    Each runtime is published to artifacts/publish/<rid>; Windows yields a single .exe next to it,
    Linux a .tar.gz. No .NET runtime is needed on the target machine.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -Runtime win-x64
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string[]] $Runtime = @('win-x64', 'linux-x64'),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src/BootVideoManager.App/BootVideoManager.App.csproj'
$output = Join-Path $root 'artifacts/publish'

if (-not $SkipTests) {
    dotnet test --solution (Join-Path $root 'BootVideoManager.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed: nothing was published.' }
}

$version = (dotnet msbuild $app -getProperty:Version).Trim()

foreach ($rid in $Runtime) {
    $dir = Join-Path $output $rid
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }

    dotnet publish $app -c Release -r $rid --self-contained -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }

    $archive = Join-Path $output "BootVideoManager-$version-$rid"
    if ($rid -like 'win-*') {
        # Single-file publish (see the App .csproj): the .exe alone is the whole application.
        Copy-Item (Join-Path $dir 'BootVideoManager.exe') "$archive.exe" -Force
        Write-Host "Created $archive.exe"
    }
    else {
        # Built on Windows the executable bit is lost: run `chmod +x BootVideoManager` after extracting,
        # or use build/publish.sh on Linux.
        tar -czf "$archive.tar.gz" -C $dir .
        Write-Host "Created $archive.tar.gz"
    }
}
