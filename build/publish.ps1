<#
.SYNOPSIS
    Runs the tests, then builds self-contained release archives of Boot Video Manager.

.DESCRIPTION
    Each runtime is published to artifacts/publish/<rid>. Windows yields an installer
    (BootVideoManager-<version>-<rid>-setup.exe, built with Inno Setup 6 from a regular folder publish) and a
    portable single .exe; Linux yields a .tar.gz. No .NET runtime is needed on the target machine.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -Runtime win-x64
    ./build/publish.ps1 -Runtime win-x64 -SkipInstaller
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string[]] $Runtime = @('win-x64', 'linux-x64'),
    [switch] $SkipTests,
    # Skips the Windows installer (Inno Setup 6 is then not required).
    [switch] $SkipInstaller
)

function Find-InnoCompiler {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $candidates = @(
        $env:ISCC,
        $(if ($onPath) { $onPath.Source }),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) {
        throw 'Inno Setup 6 (ISCC.exe) is required for the Windows installer: winget install JRSoftware.InnoSetup, set $env:ISCC, or pass -SkipInstaller.'
    }
    return $found
}

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
        # Portable: single-file publish (see the App .csproj), the .exe alone is the whole application.
        Copy-Item (Join-Path $dir 'BootVideoManager.exe') "$archive-portable.exe" -Force
        Write-Host "Created $archive-portable.exe"

        if (-not $SkipInstaller) {
            # Installer: a regular folder publish, so every component is installed on disk instead of being
            # extracted to %TEMP% at each launch.
            $setupDir = Join-Path $output "$rid-setup"
            if (Test-Path $setupDir) { Remove-Item -Recurse -Force $setupDir }
            dotnet publish $app -c Release -r $rid --self-contained -p:PublishSingleFile=false -o $setupDir
            if ($LASTEXITCODE -ne 0) { throw "Installer publish failed for $rid." }

            $iscc = Find-InnoCompiler
            $arch = $rid.Substring(4)
            & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$setupDir" "/DOutputDir=$output" "/DArch=$arch" (Join-Path $root 'packaging/windows/BootVideoManager.iss')
            if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $rid." }
            Write-Host "Created $archive-setup.exe"
        }
    }
    else {
        # Built on Windows the executable bit is lost: run `chmod +x BootVideoManager` after extracting,
        # or use build/publish.sh on Linux.
        tar -czf "$archive.tar.gz" -C $dir .
        Write-Host "Created $archive.tar.gz"
    }
}
