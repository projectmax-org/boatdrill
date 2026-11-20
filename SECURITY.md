# Security

Boatdrill runs as the signed-in user, reads the folders you name and the backups you point it at, and never
writes into a backup. It has no network code, no network listener and no update mechanism; restic, kopia
and rclone, which it runs to read those kinds of backup, reach the network as they do when you run them
yourself.

What it writes, and where:

- Its own data folder, `%LOCALAPPDATA%\Boatdrill` (or the one `BOATDRILL_DATA_DIR` names): the settings,
  whose passwords are encrypted with DPAPI for your Windows account, the drill history, the hash ledger, the
  last scan and rehearsal, the log, and the scratch folder that drills, scans and rehearsals work in and
  empty when they end.
- The startup entry: a value named `Boatdrill` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
  made on the first run unless starting with Windows is switched off. Switching it on or off in Settings
  also deletes Task Manager's own setting for it, under `...\Explorer\StartupApproved\Run`.
- With canary files on (they are off unless you turn them on), a file named `Boatdrill canary.txt` in each
  protected folder.
- A report you save, where you save it.
- For a Kopia repository, a read-only connection of Boatdrill's own: in a folder in `%TEMP%` (`$TMPDIR` or
  `/tmp` on Linux and macOS) while it checks that the repository can be reached, and in the scratch folder
  while a drill, scan or rehearsal reads it. Each is deleted when it is done.
- Whatever restic, kopia and rclone keep for themselves. Boatdrill does not turn off restic's cache, so
  restic keeps it where it always does (`%LOCALAPPDATA%\restic` on Windows, unless you have moved it), and
  rclone runs with your own rclone config, which it may update, as it does when it renews a remote's
  sign-in.

boatdrill-cli writes its scratch folder (`%TEMP%\boatdrill` on Windows; `~/.cache/boatdrill`, or `boatdrill`
in `$XDG_CACHE_HOME`, on Linux and macOS; or a `Boatdrill scratch` folder in the one `--scratch` names), the
files you name with `--history`, `--ledger` and `--html` (`report` writes `boatdrill-report.html` when none
is named), and the canary files with `--canaries`. `Boatdrill.exe --capture <folder>`, which draws every
view for screenshots, writes its pictures there and works in folders in `%TEMP%` that it deletes afterwards.

If you find something that undermines any of that, please report it privately rather than in a public
issue: use GitHub's **Report a vulnerability** button on the repository's Security tab. You will get an
acknowledgement within a few days and a fix or a clear answer as soon as one exists.

Things that are not security issues: a verdict you disagree with (open a bug), or the "Unknown
publisher" warning on an unsigned build.
