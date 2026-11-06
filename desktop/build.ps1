param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [string]$EnginePath,
    [string]$CargoPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'PixelSnapper.App\PixelSnapper.App.csproj'
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version must be MAJOR.MINOR.PATCH.' }

function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit code $LASTEXITCODE." }
}

Push-Location $repoRoot
try {
    if (!$EnginePath) {
        if (!$CargoPath) {
            $command = Get-Command cargo -ErrorAction SilentlyContinue
            if ($command) { $CargoPath = $command.Source }
            elseif ($env:CARGO_HOME -and (Test-Path -LiteralPath (Join-Path $env:CARGO_HOME 'bin\cargo.exe'))) { $CargoPath = Join-Path $env:CARGO_HOME 'bin\cargo.exe' }
            else { throw 'Rust cargo was not found. Install Rust or pass -CargoPath / -EnginePath.' }
        }
        if (!$SkipTests) { Invoke-Checked $CargoPath @('test', '--locked') }
        Invoke-Checked $CargoPath @('build', '--release', '--locked')
        $EnginePath = Join-Path $repoRoot 'target\release\spritefusion-pixel-snapper.exe'
    }
    $EnginePath = (Resolve-Path -LiteralPath $EnginePath).Path
    $assets = Join-Path $PSScriptRoot 'PixelSnapper.App\Assets'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    $embeddedEngine = Join-Path $assets 'spritefusion-pixel-snapper.exe'
    if (![string]::Equals($EnginePath, $embeddedEngine, [StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $EnginePath -Destination $embeddedEngine -Force }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $assets 'SpriteFusion-LICENSE.txt') -Force
    if (!$SkipTests) {
        Invoke-Checked 'dotnet' @('run', '--project', (Join-Path $PSScriptRoot 'PixelSnapper.Tests\PixelSnapper.Tests.csproj'), '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', (Join-Path $PSScriptRoot 'PixelSnapper.UpdateTests\PixelSnapper.UpdateTests.csproj'), '-c', 'Release')
    }
    $publishDirectory = Join-Path $PSScriptRoot ('.build\' + [Guid]::NewGuid().ToString('N'))
    Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publishDirectory, "-p:Version=$Version", '--nologo')
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $exe = Join-Path $OutputDirectory 'PixelSnapper.exe'
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'PixelSnapper.exe') -Destination $exe -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'SpriteFusion-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
    $checksum = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), "$checksum  PixelSnapper.exe`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Built PixelSnapper $Version -> $exe"
} finally { Pop-Location }
