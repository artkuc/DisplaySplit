# Contributing to DisplaySplit

Thanks for your interest! Bug reports and ideas are very welcome as
[issues](https://github.com/artkuc/DisplaySplit/issues/new/choose).

> **Pull requests:** this repository only accepts pull requests from its collaborators. If you'd like a change, please
> open an issue describing it. If you have a fix, describe it there or link to a branch in your fork. You're also free to
> fork the project under the MIT license.

## Reporting bugs

Please use the [bug report template](https://github.com/artkuc/DisplaySplit/issues/new/choose) and include:

- the DisplaySplit version (tray menu, first line);
- your Windows version, monitor resolution(s) and scaling (for example 5120×2160 @ 150 %);
- the app the problem happens with (for example "VS Code 1.95", "Explorer");
- steps to reproduce, plus what you expected and what happened instead.

A verbose log helps a lot:

1. Exit DisplaySplit from the tray.
2. Set `DISPLAYSPLIT_DEBUG=1` and start it again.
3. Reproduce the problem.
4. Attach `%APPDATA%\DisplaySplit\log.txt`. Check it first: it contains window titles.

## Development setup

This section is for collaborators, and for anyone building or modifying their own fork.

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: Visual Studio with the *Desktop development with C++* workload, only needed for NativeAOT builds

```powershell
dotnet build DisplaySplit.slnx
dotnet test  tests/DisplaySplit.Tests
```

To test against a scratch settings folder, so your real layouts and remembered positions stay untouched:

```powershell
$env:DISPLAYSPLIT_HOME = "$env:TEMP\displaysplit-dev"
$env:DISPLAYSPLIT_DEBUG = "1"
dotnet run --project src/DisplaySplit
```

### Tests

- `tests/DisplaySplit.Tests` holds the xUnit unit tests for the core logic. They run in CI on every push and pull
  request.
- `tests/E2E` is an end-to-end check against the real app. Exit any running DisplaySplit instance first. Run it
  locally with `dotnet run --project tests/E2E`.
  - It moves the real mouse for a few seconds.
  - On a locked or headless desktop, or with `--simulate`, it injects the hook's messages instead of using the mouse.
- `DisplaySplit.exe --snapshot-editor <dir>` renders the editor offscreen to PNG files and records frame timings.
  Use it for UI changes.

## Project layout

```
src/DisplaySplit/
  Core/       pure, unit-tested logic: layout tree, presets, double-click detector, restore policy, models
  Platform/   Win32 services: mouse hook + caption probe, snap engine, placement worker, monitors, storage
  UI/         tray icon, layout editor overlay (GDI+ on a per-pixel-alpha layered window), drag overlay
  Native/     hand-written P/Invoke (LibraryImport, NativeAOT-compatible)
  Engine.cs   window tracking, slots, restore-on-launch
  Controller.cs  tray, hotkeys, editor lifecycle, Shift-drag
docs/PLAN.md  design document, including the review history
```

## Guidelines

- **Keep it light.** No UI frameworks, no idle timers or polling, and no allocations in the mouse-hook callback.
- **Keep it NativeAOT-compatible.**
  - Use `LibraryImport`, not `DllImport`.
  - Use function pointers and `UnmanagedCallersOnly` for callbacks.
  - Use System.Text.Json source generation.
  - Don't use reflection.
- **Never block the UI thread on another process.** Calls into foreign windows that can block go through
  `PlacementWorker`.
- **Test the core logic.** Pure logic goes in `Core/` with unit tests.
- **Keep the style consistent.** Follow `.editorconfig` and the existing code style.
- **One topic per pull request** (collaborators). Describe what you tested, and include before/after screenshots for
  UI changes.

## Releasing (maintainers)

1. Update `CHANGELOG.md`, moving the *Unreleased* entries under the new version.
2. Tag the release and push the tag:
   ```
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. The *Release* workflow builds, tests, packages and publishes the GitHub release.

Contributions to this repository are licensed under the [MIT License](LICENSE).
