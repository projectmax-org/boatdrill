<#
.SYNOPSIS
    Writes Boatdrill's winget manifests for one release, from the templates in packaging\winget.

.DESCRIPTION
    Fills in the version, the installer's download URL and its SHA-256, and writes the manifests in the
    folder layout of microsoft/winget-pkgs (manifests\p\ProjectMax\Boatdrill\<version>), ready to be
    validated with `winget validate` and submitted there. The release workflow runs this for every tag.

.EXAMPLE
    .\packaging\New-WingetManifest.ps1 -Version 1.0.0 -Installer artifacts\installer\Boatdrill-Setup-1.0.0.exe -OutputDirectory artifacts\winget
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    # The installer as it is published: the manifest carries its SHA-256 and its download URL.
    [Parameter(Mandatory)]
    [string]$Installer,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$Repository = 'projectmax-org/project_01',

    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'

$values = @{
    Version         = $Version
    InstallerUrl    = "https://github.com/$Repository/releases/download/v$Version/$(Split-Path $Installer -Leaf)"
    InstallerSha256 = (Get-FileHash $Installer -Algorithm SHA256).Hash
    ReleaseDate     = $ReleaseDate
    ReleaseNotesUrl = "https://github.com/$Repository/releases/tag/v$Version"
    Repository      = $Repository
}

$target = Join-Path $OutputDirectory "manifests/p/ProjectMax/Boatdrill/$Version"
New-Item -ItemType Directory -Force $target | Out-Null

foreach ($template in Get-ChildItem (Join-Path $PSScriptRoot 'winget') -Filter *.yaml) {
    $text = Get-Content $template.FullName -Raw -Encoding utf8
    foreach ($key in $values.Keys) { $text = $text.Replace("{{$key}}", $values[$key]) }
    if ($text -match '\{\{\w+\}\}') { throw "$($template.Name) has a placeholder with no value: $($Matches[0])" }

    # UTF-8 without a byte order mark, as winget-pkgs takes them.
    [IO.File]::WriteAllText((Join-Path $target $template.Name), $text, [Text.UTF8Encoding]::new($false))
}

Write-Host "winget manifests for Boatdrill $Version are in $target"
