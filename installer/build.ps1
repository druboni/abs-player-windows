# Builds the installer: runs the tests, publishes a self-contained Release build, then compiles the Inno Setup script.
# Output: installer\Output\Homeshelf-<version>-Setup.exe
#
#   pwsh installer\build.ps1            # tests + build
#   pwsh installer\build.ps1 -SkipTests

param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found at $iscc" }

$version = ([xml](Get-Content "$root\src\AbsPlayer.App\AbsPlayer.App.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Homeshelf $version"

if (-not $SkipTests) {
    dotnet test "$root\tests\AbsPlayer.Core.Tests" -c Release
    if ($LASTEXITCODE) { throw "Tests failed" }
}

$publish = "$root\publish\win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\AbsPlayer.App" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $publish
if ($LASTEXITCODE) { throw "Publish failed" }

& $iscc "/DMyAppVersion=$version" "$PSScriptRoot\Homeshelf.iss"
if ($LASTEXITCODE) { throw "Inno Setup failed" }

Get-Item "$PSScriptRoot\Output\Homeshelf-$version-Setup.exe" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
