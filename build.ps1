<#
.SYNOPSIS
    Tests Boatdrill and publishes single self-contained executables to dist\.

.DESCRIPTION
    Runs the Core tests first, then publishes dist\Boatdrill.exe (the Windows app) and
    dist\boatdrill-cli.exe (the command-line runner). Self-contained by default so the files run on a
    clean Windows machine with no .NET runtime installed; -FrameworkDependent for small builds when the
    target already has the .NET Desktop Runtime.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Run
    .\build.ps1 -Capture
    .\build.ps1 -Icon
    .\build.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # Defaults to dist next to this script.
    [string]$Output,

    # Smaller output, but the target machine must already have the .NET Desktop Runtime.
    [switch]$FrameworkDependent,

    # Regenerate src\Boatdrill\Assets\app.ico and logo-512.png from tools\IconGen first.
    [switch]$Icon,

    # Afterwards, render every view with sample data to artifacts\screens (what CI does on each push).
    [switch]$Capture,

    # Launch the published app when the build succeeds.
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Output) { $Output = Join-Path $root 'dist' }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

if ($Icon) {
    Write-Host 'Drawing the icon...'
    dotnet run --project (Join-Path $root 'tools/IconGen') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'IconGen failed.' }
}

Write-Host 'Testing Boatdrill.Core...'
dotnet test (Join-Path $root 'tests/Boatdrill.Core.Tests') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

foreach ($project in 'src/Boatdrill/Boatdrill.csproj', 'src/Boatdrill.Cli/Boatdrill.Cli.csproj') {
    Write-Host "Publishing $project ($Configuration, $Runtime, self-contained: $selfContained)..."
    dotnet publish (Join-Path $root $project) -c $Configuration -r $Runtime --self-contained $selfContained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Get-ChildItem $Output -Filter *.exe | ForEach-Object { Write-Host ('  {0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB)) }

if ($Capture) {
    $screens = Join-Path $root 'artifacts/screens'
    Write-Host "Capturing the views to $screens..."
    $app = Start-Process -FilePath (Join-Path $Output 'Boatdrill.exe') -ArgumentList '--capture', $screens -PassThru -Wait
    if ($app.ExitCode -ne 0) { throw "The view capture failed with exit code $($app.ExitCode); see $screens." }
}

if ($Run) { Start-Process (Join-Path $Output 'Boatdrill.exe') }
