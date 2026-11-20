# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-11-20

First public release.

### Added

- **Zones**: split each monitor into zones with a full-screen overlay editor. Click to split (Shift for a
  horizontal split), drag dividers to resize (they snap to 1/12ths; Ctrl turns snapping off), and use × or right-click
  to remove. The editor has 8 presets, named layouts, gaps (0/4/8/16 px), undo, and a help card listing all shortcuts
  (F1 or **?**).
- **Double-click a title bar**: the window snaps into the zone under the cursor. Double-click again to restore its
  previous size.
- **Shift + double-click a title bar**: maximize the window, or restore it.
- **Shift + drag**: drop a window into the highlighted zone.
- **Remembered positions**: apps reopen in their zones after a relaunch or reboot.
  - Apps are matched by AUMID or a normalised exe path, so updates don't break the match.
  - Multi-window apps are matched by window title.
  - Each layout remembers its own zone for every window.
  - Windows already open at logon are arranged too.
- **Hotkeys**:
  - `Win+Alt+Z` opens the editor.
  - `Win+Ctrl+Alt+←/→` moves the window to the next zone.
  - `Win+Ctrl+Alt+1…9` switches layout.
- **Tray menu**: switch layouts, arrange all windows, exclude apps, pause, start with Windows.
- **Light on resources**: event-driven with no idle timers, about 9 MB private memory, and an optional NativeAOT build.

[Unreleased]: https://github.com/artkuc/DisplaySplit/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/artkuc/DisplaySplit/releases/tag/v1.0.0
