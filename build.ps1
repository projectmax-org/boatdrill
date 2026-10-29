<#
.SYNOPSIS
    Tests Boatdrill and publishes single self-contained executables to dist\.

.DESCRIPTION
    Runs the Core tests first, then publishes dist\Boatdrill.exe (the Windows app) and
    dist\boatdrill-cli.exe (the command-line runner). Self-contained by default so the files run on a
    clean Windows machine with no .NET runtime installed; -FrameworkDependent for small builds when the
    target already has the .NET Desktop Runtime.

    -Installer then builds artifacts\installer\Boatdrill-Setup-<version>.exe with Inno Setup
    (installer\Boatdrill.iss). -SignCommand signs both programs and the installer: a command line in
    which $f stands for the quoted file name, as Inno Setup's Sign Tools take it, for example
    'signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a $f'.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Run
    .\build.ps1 -Capture
    .\build.ps1 -Icon
    .\build.ps1 -FrameworkDependent
    .\build.ps1 -Installer
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
    [switch]$Run,

    # Afterwards, build the installer with Inno Setup into artifacts\installer.
    [switch]$Installer,

    # Sign the programs (and the installer) with this command; $f is the quoted file name.
    [string]$SignCommand
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

# Runs a Sign Tool command line on one file: $f is the quoted file name, $q a quote, $$ a dollar sign.
function Invoke-Sign([string]$File) {
    $quoted = '"' + (Resolve-Path $File).Path + '"'
    $command = $SignCommand.Replace('$$', "`0").Replace('$f', $quoted).Replace('$q', '"').Replace("`0", '$')
    Write-Host "Signing $File..."
    cmd /c $command
    if ($LASTEXITCODE -ne 0) { throw "Signing $File failed." }
}

# ISCC on the PATH, else where Inno Setup's own installer recorded it, newest version first.
function Find-Iscc {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $uninstallKeys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    $found = foreach ($key in $uninstallKeys) {
        Get-ChildItem $key -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Inno Setup *_is1' } | ForEach-Object {
            $location = $_.GetValue('InstallLocation')
            if ($location) { Join-Path $location 'ISCC.exe' }
        }
    }

    $iscc = $found | Where-Object { Test-Path $_ } | Sort-Object -Descending | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup was not found. Install it (winget install JRSoftware.InnoSetup), or put ISCC.exe on the PATH.' }
    return $iscc
}

if ($SignCommand) {
    foreach ($program in 'Boatdrill.exe', 'boatdrill-cli.exe') { Invoke-Sign (Join-Path $Output $program) }
}

if ($Installer) {
    $iscc = Find-Iscc
    $version = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version' | Select-Object -First 1).Node.InnerText
    $arguments = @("/DAppVersion=$version", "/DDistDir=$Output", "/DOutputDir=$(Join-Path $root 'artifacts\installer')")
    # Setup, its uninstaller and its temporary copies are signed by Inno Setup itself, with the same command.
    if ($SignCommand) { $arguments += "/Sboatdrill=$SignCommand", '/DSignTool=boatdrill' }

    Write-Host "Building the installer for Boatdrill $version with $iscc..."
    & $iscc @arguments (Join-Path $root 'installer\Boatdrill.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
}

if ($Capture) {
    $screens = Join-Path $root 'artifacts/screens'
    Write-Host "Capturing the views to $screens..."
    $app = Start-Process -FilePath (Join-Path $Output 'Boatdrill.exe') -ArgumentList '--capture', $screens -PassThru -Wait
    if ($app.ExitCode -ne 0) { throw "The view capture failed with exit code $($app.ExitCode); see $screens." }
}

if ($Run) { Start-Process (Join-Path $Output 'Boatdrill.exe') }
