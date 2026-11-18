# Releasing Boatdrill

A release is a tag. Everything after the tag is done by `.github/workflows/release.yml`, and nothing
reaches users until a maintainer publishes the draft it makes.

## Make a release

1. Set the version in `Directory.Build.props`, the only place it lives:

   ```xml
   <Version>1.0.0</Version>
   ```

   Commit that on `main` and let CI pass. The Windows job already builds the installer on every push, so
   a release does not discover a broken installer script.

2. Tag the commit and push the tag:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. The release workflow checks the tag against `<Version>` and stops if they differ. Then it:

   - runs the tests on Windows, with restic, rclone and 7-Zip installed;
   - publishes `Boatdrill.exe` and `boatdrill-cli.exe`, self-contained, and signs them if signing is set up;
   - builds `Boatdrill-Setup-<version>.exe` with Inno Setup from `installer/Boatdrill.iss`;
   - writes `SHA256SUMS.txt` for the three files, in `sha256sum` format;
   - attests the files' build provenance, so anyone can check they came from this repository's workflow;
   - writes the winget manifests for the release, kept as the workflow artifact `winget-manifests-<version>`;
   - drafts a GitHub release named "Boatdrill <version>" with the files, the checksums and notes.

4. Read the draft on the Releases page. Add what changed in words a user would use, then publish it.

Running the workflow again for the same tag replaces the files on its release.

## What users get

| File | What it is |
| --- | --- |
| `Boatdrill-Setup-<version>.exe` | The installer. Installs for the current user, no administrator needed; an administrator can choose all users, or pass `/ALLUSERS`. Silent: `/VERYSILENT`. |
| `Boatdrill-<version>-portable-x64.exe` | The app as one file, run from anywhere. |
| `boatdrill-cli-<version>-x64.exe` | The command-line runner. |
| `SHA256SUMS.txt` | Their SHA-256 checksums. |

The installer asks a running Boatdrill to exit before it replaces it (`Boatdrill.exe --quit`) and starts
it again afterwards, in the notification area, if it was running. Uninstalling removes the startup
entry and asks whether to delete the settings and drill history in `%LOCALAPPDATA%\Boatdrill`; a silent
uninstall keeps them.

To check a download:

```powershell
Get-FileHash .\Boatdrill-Setup-1.0.0.exe -Algorithm SHA256
gh attestation verify .\Boatdrill-Setup-1.0.0.exe --repo projectmax-org/project_01
```

## winget

The package identifier is `ProjectMax.Boatdrill`. For each release, the workflow writes its manifests
(English and Korean descriptions) in the folder layout of
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). After publishing the release:

1. Download the `winget-manifests-<version>` artifact from the release workflow's run.
2. Check them on a Windows machine, then try installing from them:

   ```powershell
   winget validate --manifest .\manifests\p\ProjectMax\Boatdrill\1.0.0
   winget install --manifest .\manifests\p\ProjectMax\Boatdrill\1.0.0
   ```

3. Copy the folder into a fork of microsoft/winget-pkgs at the same path, and open a pull request there.
   `wingetcreate submit <folder>` does the fork and the pull request in one step.

The manifests point at the installer on the published release, so they only work once it is public.

To change the descriptions, edit the templates in `packaging/winget/`; `packaging/New-WingetManifest.ps1`
fills in the version, the URL and the checksum.

## Code signing

Not set up yet: it needs a certificate, and a decision on whose name it carries. Until then the release
notes say the files are unsigned, and Windows SmartScreen may warn when they first run.

When a signing service is chosen, set the repository variable `SIGN_COMMAND` (Settings, Secrets and
variables, Actions, Variables) to a command line that signs one file, with `$f` where the quoted file
name goes. That is the format Inno Setup's Sign Tools take, and the workflow uses the same command for
the two programs, Setup, its uninstaller and its temporary copies. Inno Setup runs it itself, without
cmd.exe and from its own folder, so start it with a program on the PATH or a full path, give any other
file by its full path, and leave out %VARIABLES%, pipes and redirection; a signing service that needs a
secret reads it from the environment. For example, with a certificate that the runner can reach through
the Windows certificate store:

```text
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <certificate thumbprint> $f
```

`$q` stands for a quotation mark and `$$` for a dollar sign. The step that runs the command can also read
the secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` and `SIGN_SECRET` from its
environment, for services that take their credentials that way. Locally:

```powershell
.\build.ps1 -Installer -SignCommand 'signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a $f'
```

## Building the installer locally

Install [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3 or later (`winget install JRSoftware.InnoSetup`),
then:

```powershell
.\build.ps1 -Installer
```

The installer lands in `artifacts\installer\`. The installer is built with Inno Setup, by Jordan Russell
and Martijn Laan.
