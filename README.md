# Boatdrill

A backup you have never restored from is a hope, not a backup. Boatdrill is a small Windows app that
rehearses restoring from your backups on a schedule and says, in plain words, whether they would work.

Boatdrill is a [Project Max](https://projectmax-org.github.io) application: free, open source under the
[MIT licence](LICENSE), no account, no telemetry. Version 0.1 is in development and has no release yet.
Every push builds `Boatdrill.exe` and `boatdrill-cli.exe`, which you can download from the run's
artifacts on the [Actions page](https://github.com/projectmax-org/project_01/actions).

---

## What it does

**Reads your backups back.** You name the folders you care about (sources) and the places their copies
should exist (destinations): an external drive, a NAS share, a OneDrive or Dropbox folder. A drill picks a
sample of real files, finds their copies, reads every byte back into a scratch folder on this PC, compares
SHA-256 hashes with the originals, and times the read.

**Says what it found.** One score and one sentence per destination, such as *"Backup drive would restore.
397 of 400 sampled files read back correctly and 3 are older than the original. The oldest change not yet
copied here is from 26 October 2026 (1 day ago). A full restore of 212.3 GB would take about 38 min at
the speed measured."* The score has four parts that are always shown: coverage, freshness, integrity
and reachability. [docs/SCORING.md](docs/SCORING.md) has the arithmetic.

**Drills on a schedule, when you are away.** Daily, weekly or monthly, counted from the last drill. A
due drill waits until the keyboard and mouse have been idle for five minutes, waits for mains power on a
laptop, and never runs during a game or a presentation. It runs at Windows' background priority, and a
drill that has waited a day (or three days for power) goes ahead at the next chance.

**Speaks up only when it matters.** A Windows notification when a drill finds a backup that would not
restore, could not be reached, or needs attention. Silence when everything would restore.

**Works out where your copies are.** Whether the drive holds the folder's contents directly, a folder
named after it, or a full-path mirror (`D:\Backup\C\Users\...`), the drill tries each layout against the
sample and keeps the one that fits. Settings can fix the layout per destination.

**Tells corruption from lag.** A copy that is older than the original is *behind*; a copy that is not
older yet has different bytes is *corrupt*, and any corruption keeps the score out of the green. A cloud
placeholder whose bytes are not on the disk is reported as exactly that, never downloaded.

**Stays honest about a sample.** Every coverage figure carries its margin of error, the sample size and the
byte cap are settings, and recently changed files are drawn more often because they are the ones a backup
is most likely to be missing.

## What it never does

- It never writes into a destination. The one place a drill writes is its scratch folder on this PC,
  which is emptied when the drill ends. (A planned, opt-in canary file goes into *source* folders only.)
- It never phones home. There is no network code in the app at all.
- It never pulls a cloud placeholder down from OneDrive or Dropbox to check it.
- It never polls. Between drills there is one timer, armed for the moment the next drill is due.

---

## Using the app

The first time it starts, Boatdrill suggests Documents, Pictures and the desktop as the folders to
protect. Add the place their backup goes under **Settings**, and either run a drill straight away or let
the schedule run one the next time you step away.

**Backups** shows a card per destination: the score, the verdict in a sentence, the four parts of the score,
and the files that did not verify. **History** lists past drills with each backup's score, which is where a
backup that keeps dropping out or slowly falls behind shows first. **Settings** holds the folders, the
destinations, the schedule and how much a drill reads.

Closing the window leaves Boatdrill in the notification area, where scheduled drills run; quit from the
icon's menu. It starts with Windows by default, because a scheduled drill can only run while the app does;
switch that off in Settings or in Task Manager's Startup tab.

Settings and drill history live in `%LOCALAPPDATA%\Boatdrill`: `settings.json`, `history.jsonl` (one
drill per line) and `boatdrill.log`.

## The command-line runner

The same engine from a terminal, on Windows, Linux and macOS, with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```
dotnet run --project src/Boatdrill.Cli -- drill --source ~/Documents --dest /mnt/backup/Documents
dotnet run --project src/Boatdrill.Cli -- drill --source C:\Users\you\Pictures --dest E:\ --sample 300 --json
```

`--source` and `--dest` repeat. `--history <file>` appends the report to a JSON-lines file, which is what
makes the reachability part of the score meaningful over time. Exit code 0 means every destination would
restore (fully or mostly), 1 means at least one would not, 2 means bad arguments.

## Building

```powershell
.\build.ps1           # tests, then dist\Boatdrill.exe and dist\boatdrill-cli.exe, self-contained
.\build.ps1 -Run      # and launch the app
.\build.ps1 -Capture  # and render every view with sample data to artifacts\screens
.\build.ps1 -Icon     # redraw the icon first (tools/IconGen)
dotnet run --project src/Boatdrill
```

The engine, its tests, the command-line runner and the icon generator build and run on any OS; the app
compiles anywhere and runs on Windows 10 and 11. `Boatdrill.exe --capture <folder>` loads every view
against sample results, writes a PNG of each in the light and the dark theme, and fails on a XAML error or
a binding to a missing property. CI runs it on every push and keeps the images as an artifact. On Linux,
`tools/capture-under-wine.sh` does the same under Wine, with Liberation Sans standing in for Segoe UI.

## How it's put together

```
src/Boatdrill.Core/          the engine (net10.0: no UI, no Windows API)
  Sampling/                  one-pass weighted reservoir sampling, byte cap, per-folder floor
  Adapters/                  IDestinationAdapter and MirrorFolderAdapter (layout detection, read-back)
  Drill/                     DrillRunner, the report model, the sentence writer
  Scoring/                   the four-part readiness score and its margin of error
  Scheduling/                when the next drill is due, and when it should wait
  Notifications/             what a finished drill says in a notification, if anything
  Settings/ History/         settings.json and history.jsonl
src/Boatdrill/               the WPF app (net10.0-windows)
  Drilling/                  DrillService (one drill at a time) and DrillScheduler (one timer)
  Platform/                  Windows: idle, power and full-screen checks, startup entry, log, paths
  Tray/ UI/                  notification-area icon and menu, views, view models, theme
  Capture/                   --capture: sample data and the view renderer
src/Boatdrill.Cli/           boatdrill-cli, the same engine from a terminal
tests/Boatdrill.Core.Tests/  xunit; every drill runs against temp folders, including deliberately broken ones
tools/IconGen/               draws app.ico and the title-bar logo with plain arithmetic
```

A drill is a pipeline: **Sampler** picks files, an **adapter** finds their copies and reads them back
into scratch while hashing, the **runner** classifies each file (verified, behind, corrupt, missing,
cloud only, unreadable) and measures throughput, the **scorer** turns the counts into the score, and
**Verdicts** writes the sentence. Everything that decides something lives in Core and is tested on Linux
in CI before the app is built and captured on Windows.

The app follows Driftwall's patterns, and some of its code: the notification-area icon is written against
Shell_NotifyIcon directly, the theme swaps one palette dictionary, and nothing runs between scheduled events.

## Roadmap

- **v0.1** folder-mirror destinations, the app with its schedule, tray icon and notifications. Built;
  waiting for a test against a real external drive.
- **v0.2** opt-in canary files that measure backup lag directly, a hash ledger across drills to catch bit
  rot, the "what exists only on this PC" view, Windows File History and restic/Kopia destinations, and
  repository passwords kept with DPAPI.
- **v1.0** installer, winget, code signing, exportable report.

## Licence

MIT. See [LICENSE](LICENSE).
