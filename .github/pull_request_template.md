## What and why

<!-- What does this change, and why? Link the issue it fixes, if any (e.g. "Fixes #12"). -->

## How I tested it

<!-- Windows version, monitor setup, apps you tried. Add screenshots for UI changes. -->

## Checklist

- [ ] `dotnet build DisplaySplit.slnx` has no warnings
- [ ] `dotnet test tests/DisplaySplit.Tests` passes
- [ ] No new idle timers/polling, and nothing blocking in the mouse-hook callback
- [ ] NativeAOT-compatible (no reflection, `LibraryImport`, source-generated JSON)
- [ ] `CHANGELOG.md` updated under *Unreleased* (for user-visible changes)
