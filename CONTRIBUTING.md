# Contributing to Boatdrill

Thanks for looking. Boatdrill is a small, opinionated app and the bar for changes is "would a careful
person who relies on it want this". Bug reports, source fixes and well-argued features are all welcome.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The engine and its tests build on any
OS; the Windows app needs Windows to run (it compiles anywhere).

```
dotnet test tests/Boatdrill.Core.Tests          # the engine
dotnet run --project src/Boatdrill.Cli -- drill --source <folder> --dest <folder>
.\build.ps1 -Capture                            # Windows: tests, dist\Boatdrill.exe, screenshots of every view
```

The restic tests drive the real restic and are skipped without it: install it (`apt install restic`,
`choco install restic`, `brew install restic`) or point `BOATDRILL_RESTIC` at the executable.

`README.md` describes the layout. The short version: Core decides (sampling, adapters, drills, scores,
the schedule, notifications) and is tested anywhere; the app shows and schedules, and is Windows only.

## Before you open a pull request

- `dotnet test tests/Boatdrill.Core.Tests` passes. New behaviour comes with a test; drills run against
  temp folders built by `TempTree`, including deliberately broken ones.
- Use the thing you changed, in the app or through `boatdrill-cli`. For a change to a view, run
  `.\build.ps1 -Capture` (or `tools/capture-under-wine.sh` on Linux) and attach the images from
  `artifacts/screens` for both themes.
- Keep the two promises. A drill never writes into a destination, and the app does nothing between
  drills: a change that adds a timer, a polling loop or a background thread needs a very good reason.
- One change per pull request, described in the first line of the description the way you would explain
  it to a user.

## Adding a destination kind

Implement `IDestinationAdapter` (start from `MirrorFolderAdapter`, or `ResticAdapter` for a tool-backed
repository), add a `DestinationKind`, register the adapter in `DrillRunner`'s default list, and add tests
that build the destination under a temp folder. An adapter reads; the only places it may write are the
scratch folder and the scratch file it is handed. Give each copy a stable `Identity` when its path is a
scratch file, so the hash ledger can recognise it next time, and say `ChecksItsOwnBytes` when the tool
behind it verifies what it reads.

## Reporting bugs

Use the bug template. The drill's sentence and score, or the output of `boatdrill-cli drill --json`, is
usually the fastest way to the cause; strip anything private before pasting.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence in `LICENSE`.
