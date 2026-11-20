# DisplaySplit

A tiny Windows 10/11 tray utility that splits a (ultrawide) monitor into zones, in the spirit of DisplayFusion's
monitor splitting or FancyZones, built to be fast and light.

- **Double-click a title bar**: the window fills the zone under the cursor. Double-click again to restore its
  previous size.
- **Shift + double-click a title bar**: the window maximizes to the full monitor. Do it again to restore.
- **Hold Shift while dragging a window**: zones light up, and releasing drops the window into the highlighted zone.
- **Remembers where apps belong.** After a reboot, or when you relaunch an app, its windows reopen in the same zones.
  Multi-window apps are matched by window title.
- **Layout editor**: click to split, drag lines to resize, × to remove. Includes presets, named layouts, gaps and undo.

## Using it

| Action | How |
|---|---|
| Open the layout editor | Left-click the tray icon, press `Win+Alt+Z`, or start `DisplaySplit.exe` again |
| Show help & all shortcuts | `F1` or the **?** button in the editor, or tray → *How to use & shortcuts…* |
| Split a zone | Click inside it. Hold **Shift** for a horizontal split. The split snaps to 1/12ths of the screen; hold **Ctrl** for free placement |
| Resize zones | Drag the line between them (Ctrl = no snapping) |
| Merge / remove | Hover a line and click its **×**, or right-click it. Hover a zone and click its **×**, or right-click it |
| Save / cancel / undo | `Enter` / `Esc` / `Ctrl+Z` |
| Several layouts | `+ New` duplicates the current layout. Switch with `‹ ›`. Rename by clicking the name or pressing `F2` |
| Switch layout | Tray → Layout, or `Win+Ctrl+Alt+1…9` |
| Move window to neighbouring zone | `Win+Ctrl+Alt+←/→` |
| Re-apply remembered positions | Tray → *Arrange all windows now* |
| Stop remembering one app | Bring it to the front, then tray → *Don't remember "…"*. Undo it under *Excluded apps* |

On first launch the editor opens with a default layout: **25/50/25** on ultrawide screens (aspect ≥ 2.1), full screen
otherwise. A short welcome card explains the basics. Windows 11 puts new tray icons in the overflow (^) area, so drag
the icon onto the taskbar to keep it visible.

## Resource use

Everything is event-driven: there are no timers while idle, and the low-level mouse hook returns immediately for mouse
moves. Measured on a 5120×2160 monitor:

- Idle CPU: 0 ms over 3 s.
- Memory: about 9 MB private and about 40 MB working set (Debug build).
- Release exe: under 1 MB, single file, framework-dependent with ReadyToRun.
- Editor frame time: about 1 ms while hovering and about 6 ms while dragging a divider.

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build DisplaySplit.slnx
dotnet test  tests/DisplaySplit.Tests
dotnet publish src/DisplaySplit -c Release -o publish      # single-file exe, needs the .NET 10 runtime
```

The code is NativeAOT-compatible. If the Visual Studio "Desktop development with C++" workload is installed, this
builds a fully self-contained exe of a few MB that doesn't need the .NET runtime:

```powershell
dotnet publish src/DisplaySplit -c Release -p:PublishAot=true -o publish-aot
```

### Tests

- `tests/DisplaySplit.Tests` (xUnit) covers layout operations and invariants, presets, the double-click detector,
  restore policy and slot matching, identity normalisation, JSON persistence and corrupt-file recovery, and Win32 struct
  layouts.
- `tests/E2E` is an end-to-end check against the real app on a test window. It covers snap, restore, maximize, a
  DPI-unaware window, restore on relaunch, an app that moves itself after showing, and the `--autostart` arrange at
  logon. It drives the real mouse for a few seconds. On a locked or headless desktop it switches automatically to
  `--simulate`, which injects the hook's messages directly and checks the caption hit-testing separately.
- `DisplaySplit.exe --snapshot-editor <dir>` (with `DISPLAYSPLIT_HOME=<temp dir>`) renders editor states to PNGs and
  writes frame timings.

## Data & settings

`%APPDATA%\DisplaySplit\` contains `settings.json`, `layouts.json`, `placements.json` and `log.txt`.

- Files are written atomically (temp file, fsync, `.bak`, then move).
- If a file is corrupt, its `.bak` is used instead, and the corrupt file is kept as `*.corrupt-<time>`.
- Set `DISPLAYSPLIT_HOME` to use a different folder. Set `DISPLAYSPLIT_DEBUG=1` for verbose logging.

Autostart uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--autostart`. That flag also arranges
windows that are already open at logon.

## Known limitations

- **Admin windows**: if a window runs elevated and DisplaySplit doesn't, Windows blocks moving it. Its title bar keeps
  the normal behaviour.
- **Custom title bars**: some apps (for example the Windows 11 Explorer tab strip) only report a title bar to their top
  window. Their double-click is only taken over when the click really started a window move. Otherwise the native
  maximize happens.
- Windows' own edge snapping can also trigger while Shift-dragging at the very edge of the screen.
- Windows without a standard resize border (no `WS_THICKFRAME`, such as fixed dialogs and some custom-chrome apps)
  are centred in the zone at their current size instead of being stretched.

See `docs/PLAN.md` for the full design and the validation history.
