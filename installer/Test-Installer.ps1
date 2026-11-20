<#
.SYNOPSIS
    Installs Boatdrill silently for the current user, checks what the installer put where, uninstalls it
    silently, and checks that everything it added is gone again.

.DESCRIPTION
    The release workflow runs this on the installer it is about to offer, so the file people download has been
    installed and removed once. It refuses to run where Boatdrill is already installed, since it would replace
    that install and then remove it. Settings and drill history are never touched: a silent uninstall keeps
    them, and the program is not started, only the command-line runner, with its data kept elsewhere.

.EXAMPLE
    .\installer\Test-Installer.ps1 -Setup .\artifacts\installer\Boatdrill-Setup-1.0.0.exe
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    # Where the install and uninstall logs go.
    [string]$LogDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'boatdrill-installer-test')
)

$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
# Inno Setup pads the version in Setup's version information with spaces.
$version = (Get-Item $Setup).VersionInfo.ProductVersion.Trim([char]0, ' ')
$appId = '{BC869AFA-7D6B-4722-9A96-BFE82969B3C5}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$machineUninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Boatdrill'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Boatdrill.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Boatdrill.lnk'
$data = Join-Path $env:LOCALAPPDATA 'Boatdrill'
New-Item -ItemType Directory -Force $LogDirectory | Out-Null

$failed = 0
function Check([string]$What, [bool]$Ok) {
    Write-Host ('{0}  {1}' -f $(if ($Ok) { 'ok    ' } else { 'FAILED' }), $What)
    if (-not $Ok) { $script:failed++ }
}

function Test-StartupEntry { $null -ne (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Boatdrill }

if ((Test-Path $uninstallKey) -or (Test-Path $machineUninstallKey) -or (Test-Path $app)) {
    throw 'Boatdrill is already installed here; this test would replace that install and then remove it.'
}

$startupBefore = Test-StartupEntry
$dataBefore = Test-Path $data

Write-Host "Installing Boatdrill $version silently..."
$install = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$(Join-Path $LogDirectory 'install.log')`"" -Wait -PassThru
Check "Setup finished with exit code 0 ($($install.ExitCode))" ($install.ExitCode -eq 0)
foreach ($file in 'Boatdrill.exe', 'boatdrill-cli.exe', 'LICENSE.txt', 'unins000.exe') {
    Check "$file is in $app" (Test-Path (Join-Path $app $file))
}

$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check 'Apps and Features lists Boatdrill for this user' ($null -ne $entry -and $entry.DisplayName -eq 'Boatdrill')
Check "Apps and Features shows version $version" ($null -ne $entry -and $entry.DisplayVersion -eq $version)
Check 'nothing was installed for all users' (-not (Test-Path $machineUninstallKey))
Check 'the Start menu has a shortcut' (Test-Path $startMenu)
Check 'the desktop has none, as that box starts unticked' (-not (Test-Path $desktop))
Check 'no startup entry was added' ((Test-StartupEntry) -eq $startupBefore)

# The installed command-line runner starts. Its data folder, should it want one, is the test's own.
$env:BOATDRILL_DATA_DIR = Join-Path $LogDirectory 'data'
$usage = & (Join-Path $app 'boatdrill-cli.exe') --help 2>&1 | Out-String
Check "the installed boatdrill-cli starts (exit code $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and $usage -match 'boatdrill-cli drill')

Write-Host 'Uninstalling silently...'
# unins000.exe hands over to a copy of itself and returns at once, so the result is waited for.
Start-Process (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $LogDirectory 'uninstall.log')`"" -Wait
$deadline = (Get-Date).AddSeconds(120)
while (((Test-Path $uninstallKey) -or (Test-Path $app)) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Check 'the install folder is gone' (-not (Test-Path $app))
Check 'Apps and Features no longer lists Boatdrill' (-not (Test-Path $uninstallKey))
Check 'the Start menu shortcut is gone' (-not (Test-Path $startMenu))
Check 'the startup entry is as it was' ((Test-StartupEntry) -eq $startupBefore)
Check 'settings and history are as they were, as a silent uninstall keeps them' ((Test-Path $data) -eq $dataBefore)

if ($failed -gt 0) { throw "$failed installer check(s) failed; the logs are in $LogDirectory." }
Write-Host 'The installer installed and uninstalled cleanly.'
