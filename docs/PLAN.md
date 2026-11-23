# DisplaySplit — Solution Plan (v6, validated)

A tiny Windows tray utility that splits a (ultrawide) monitor into user-defined zones,
snaps windows into zones by double-clicking their title bar, and remembers where every
application belongs so it reappears there after reboot / relaunch.

v2 incorporates review round 1 (Win32 internals, persistence/restore, UX/perf), marked **[v2]**. v3 incorporates round 2, marked **[v3]**; v4 round 3, marked **[v4]**; v5 round 4, marked **[v5]**; v6 round 5, marked **[v6]**. Round 6 found no major issues.

## 1. Requirements

| # | Requirement | Source |
|---|-------------|--------|
| R1 | Create/edit split layouts per monitor (arbitrary vertical/horizontal splits, resizable) | user |
| R2 | Double-click a window's title bar → window fills the zone under the cursor | user |
| R3 | Shift + double-click title bar → window maximizes to the full monitor | user |
| R4 | Very intuitive UI/UX | user |
| R5 | Fast, light on CPU/RAM (event driven, no polling at idle) | user |
| R6 | After PC restart, launched programs appear in the same zones as before | user |
| R7 | Starts with Windows, single instance, tray icon | implied |

Nice-to-have (toggleable):
- N1 Shift while dragging a window shows zones; release drops window into the highlighted zone.
- N2 Hotkeys: `Win+Alt+Z` editor; `Win+Ctrl+Alt+←/→` move foreground window to neighbouring zone;
  `Win+Ctrl+Alt+1..9` switch active layout of the monitor under the cursor. **[v2]** Registration failures are shown in the tray tooltip/menu.
- N3 Double-click again on a window already filling that zone restores its pre-snap rect
  (**[v2]** fallback when unknown, e.g. after reboot: centred 60 % × 70 % of the work area).
- N4 Windows snapped to zones are re-fitted automatically when the layout is edited or switched.

## 2. Stack decision

**C# / .NET 10 (LTS), pure Win32 via `LibraryImport` P/Invoke, no WinForms/WPF, GDI+ flat API for anti-aliased drawing.**

- Toolchain present on the machine (Rust and MSVC C++ build tools are not installed; .NET 10 SDK is).
  Node/Electron would violate R5.
- Pure Win32 keeps the working set small and startup fast. Realistic targets: **20–30 MB working set idle
  (R2R)**, ~8–12 MB with NativeAOT.
- Code is written **NativeAOT-compatible** (LibraryImport, `UnmanagedCallersOnly` callbacks, function-pointer COM
  calls, System.Text.Json source generators, no reflection) so `dotnet publish -p:PublishAot=true` works once the
  "Desktop development with C++" VS workload is installed.
- Default publish: framework-dependent single-file, ReadyToRun, invariant globalization, workstation GC,
  `System.GC.ConserveMemory`. Needs the .NET 10 runtime (present). **[v2]** The README documents the AOT build as
  the zero-dependency option.
- Hand-written interop (CsWin32 needs a NuGet source generator; acceptable alternative, but hand-written keeps the
  surface small and reviewable). All structs are covered by size assertions in unit tests **[v2]**.

## 3. Architecture

```
DisplaySplit.exe (one process, 4 threads: UI, hook, probe, placement worker)
│
├─ UI thread (STA, message loop)
│   ├─ App            – startup, single-instance mutex, HIDDEN TOP-LEVEL window [v2] (not HWND_MESSAGE, so it
│   │                   receives WM_DISPLAYCHANGE, WM_SETTINGCHANGE, TaskbarCreated, WM_ENDSESSION,
│   │                   WM_POWERBROADCAST, WM_WTSSESSION_CHANGE, shell-hook messages)
│   ├─ TrayIcon       – Shell_NotifyIcon, runtime-drawn icon, context menu, re-add on TaskbarCreated
│   ├─ MonitorService – monitors, stable keys (EDID), work areas, DPI; debounced display-change handling
│   ├─ WinEventWatcher– individual WinEvent hooks (no ranges) [v2], see §8.4
│   ├─ SnapEngine     – target rects, frame compensation, placement, verification pass
│   ├─ WindowTracker  – hwnd → zone, pre-snap rects, settle sessions for new windows
│   ├─ PlacementStore – persisted per-app slots (R6)
│   ├─ LayoutStore    – per-monitor named layouts
│   ├─ EditorOverlay  – one per-pixel-alpha layered window per monitor (R1, R4)
│   ├─ DragOverlay    – per-zone LWA_ALPHA windows while Shift-dragging (N1)
│   └─ HotkeyService
├─ Hook thread (THREAD_PRIORITY_TIME_CRITICAL, own message loop)
│   └─ MouseHook – WH_MOUSE_LL, allocation-free UnmanagedCallersOnly callback, DoubleClickDetector,
│                  synchronous swallow decision using the latest probe result
├─ Probe thread [v2] (own message loop) – caption hit-tests for the hook (never blocks the hook or UI thread)
└─ Placement worker [v3] – executes every cross-process window call that can block (SetWindowPlacement, SetWindowPos,
                  ShowWindowAsync, GetWindowPlacement of foreign windows) from a FIFO queue; results are posted back to
                  the UI thread. Watchdog: a job running > 1.5 s marks the worker as stuck, a fresh worker thread takes
                  over the queue and the stuck job's window is blacklisted for 30 s (at most 4 abandoned threads;
                  beyond that the queue simply waits) **[v5]**. The UI thread never sends synchronous messages to
                  other processes.
```

### 3.1 Project layout

```
DisplaySplit.slnx
src/DisplaySplit/
  Native/      Win32.cs (structs + imports), Com.cs (IPropertyStore via function pointers), Gdip.cs (GDI+ flat)
  Core/        pure & unit-tested: Geometry, Layout(+Ops), LayoutPresets, DoubleClickDetector, Models(+JsonCtx),
               AppIdentityRules (path normalisation), SlotMatcher, RestorePolicy, EditorMath (snapping/hit-zones)
  Platform/    MonitorService, WindowInfo, AppIdentity, SnapEngine, WindowTracker, MouseHook, CaptionProbe,
               WinEventWatcher, HotkeyService, Autostart, Storage, SingleInstance
  UI/          TrayIcon, EditorOverlay, DragOverlay, Gfx (GDI+ helpers), Theme, IconFactory, HiddenWindow
tests/DisplaySplit.Tests/   xUnit
tests/E2E/                  target-window app + scripted check (interactive desktop only)
```

## 4. Layout model (R1)

Guillotine tree stored as fractions (survives resolution/DPI/taskbar changes):

```
LayoutNode = Leaf { Id } | Split { Dir: Columns|Rows, Children[], Sizes[] (sum 1) }
Layout     { Id (8-char), Name, Root, NextId }          // ids are never reused within a layout
MonitorLayouts { MonitorKey, DevicePath, W, H, Layouts: List<Layout>, ActiveId }   [v2 named layouts]
```

- Zones resolved against the monitor **work area**; cumulative rounding so zones tile exactly; global gap setting
  (0/4/8/16 px, default 0) applied as outer=gap, inner=gap/2 per side.
- Operations (pure, unit tested):
  - `SplitLeaf(id, dir, fraction)` – inserts a sibling when parent has the same direction; original id stays with the
    **larger** part (first on tie) **[v2]**; rejected below min size.
  - `MoveDivider(split, i, pos)` – clamped (min 4 % or 120 px, whichever larger in the editor).
  - `RemoveZone(id)` – **always allowed** (except the last zone) **[v2]**; space goes to previous sibling (next if first).
  - `RemoveDivider(split, i)` – two leaves merge (keep larger id); otherwise the smaller side is absorbed by the larger **[v2]**.
  - `Normalize` – collapse single-child splits, flatten same-direction nesting.
  - `Repair` – validate deserialised trees (bad sizes, duplicate ids).
- Presets (8): Full, Halves, Thirds, Focus 25/50/25, Main+side 2/3·1/3, Four columns, Centre+stacked sides, 2×2.
  Applying a preset to a layout hands out **fresh ids from NextId** **[v2]**.
- Default for aspect ≥ 2.1 (this 5120×2160 monitor): Focus 25/50/25; otherwise Full.
- Layouts of disconnected monitors are kept.

## 5. Layout editor UX (R1, R4)

Open via: tray left-click, tray "Edit layout…", `Win+Alt+Z`, launching the exe again
(**[v2]** second instance calls `AllowSetForegroundWindow(ASFW_ANY)` before signalling), and automatically on first run.

One topmost layered window per monitor covering the monitor's **work area** (taskbar stays visible and usable),
rendered into a 32-bpp top-down DIB section wrapped by GDI+ via `GdipCreateBitmapFromScan0(PixelFormat32bppPARGB, bits)` +
`GdipGetImageGraphicsContext` **[v3]** (never GDI text/drawing calls on that surface), presented with
`UpdateLayeredWindowIndirect` (per-pixel alpha). Text uses `TextRenderingHintAntiAliasGridFit` (grayscale AA) **[v3]**.
**Every pixel has alpha ≥ 10/255** (gaps included) so the overlay never becomes click-through **[v3]**.
Only the dirty rect is re-rendered/presented per mouse move (coalesced to ≤ 60 fps); during a divider drag the dirty rect is
the band swept by the divider plus old/new label rects; flat zone bodies are filled with direct pixel writes, GDI+ only for
AA edges & text; frame budget < 8 ms, measured in milestone 4 **[v3]**. DIBs/windows are destroyed when the editor
closes (no working-set trimming — it would page out the hook path) **[v3]**.

Interaction model **[v2]** (priority order for the element under the cursor):
1. **Toolbar** (top-centre pill): layout name (click to rename via inline text), `◀ ▶` switch saved layouts, `+ New`,
   preset thumbnails, Gap 0/4/8/16, Undo, Save, Cancel.
2. **Divider grab band**: ±9 logical px around every divider. Cursor `SIZEWE`/`SIZENS`, divider highlights. Drag resizes
   with live px/% read-outs; snaps to 1/12ths (hold **Ctrl** to disable snapping **[v2]**, not Alt).
   Divider midpoint shows a small **×** handle on hover → click merges. Right-click divider also merges.
3. **Zone**: hover shows a dashed split preview line following the cursor (vertical; **Shift** = horizontal) with
   resulting sizes, snapped to 1/12ths like divider drags (Ctrl disables) **[v3]**; no preview within min-size distance of an existing divider/edge. **Click** (press+release with < 4 px
   movement) splits. Corner **×** button on hover removes the zone; right-click zone also removes it. The first click after
   the overlay activates is ignored if it only activated the window.
- Zone label: big zone number + pixel size (e.g. **2** · 2560 × 2088).
- Bottom hint bar: "Click to split · Shift = horizontal · Drag a line to resize · × or right-click to remove ·
  Enter save · Esc cancel · Ctrl+Z undo".
- First run: editor opens with the default layout and a 3-step coach card (1. Click to split · 2. Drag lines ·
  3. Save, then double-click any title bar). On save, a tray balloon says "DisplaySplit is in the tray — pin it from the
  overflow (^) for quick access".
- Save → the layout currently shown (via ◀ ▶) becomes the monitor's active layout **[v3]**, refits windows snapped to
  zones (N4), and resolves stored slots per §8.2 (explicit → derived → overlap only for zone ids that no longer exist). With several monitors, Save/Cancel/Esc/Enter act on all overlays at once.

## 6. Double-click on title bar (R2, R3, N3)

### 6.1 Detection

`WH_MOUSE_LL` on the hook thread. Callback is `UnmanagedCallersOnly`, allocation-free; `WM_MOUSEMOVE` returns
immediately. The path is exercised once at startup (warm-up) to avoid first-call JIT.

1. **LBUTTONDOWN #1** → `DoubleClickDetector` returns `First(seq)`. Hook posts `(seq, pt)` to the probe thread.
   Probe thread **[v2]**:
   - `h = WindowFromPoint(pt)`; walk `h → parent → … → GA_ROOT`: for each level call (with the **physical** point; verified
     during implementation that Windows maps WM_NCHITTEST coordinates for DPI-unaware targets itself, so the [v2]
     PhysicalToLogicalPointForPerMonitorDPI pre-conversion was wrong and was removed)
     `SendMessageTimeoutW(level, WM_NCHITTEST, …, SMTO_ABORTIFHUNG|SMTO_ERRORONEXIT, 60 ms)`.
     `HTTRANSPARENT` → go up; `HTCAPTION` at any level → hit; any other result → stop. **[v5]** If the chain stopped
     at a child (not the root), ask the root once as well (Win11 Explorer / XAML-island title bars answer HTCLIENT in the
     child but HTCAPTION in the custom-frame root); only HTCAPTION counts. **[v6]** A caption found *only* through this
     root fallback needs evidence before the 2nd click is swallowed: a `MOVESIZESTART` for that root must have fired
     between press #1 and press #2 (a real caption press enters the move loop; a click on a toolbar button/tab "+"
     hosted in the child does not). The UI thread publishes `LastMoveSizeStart(root, tick)` for the hook to read.
   - Validate root (§6.3), check that we can control it (not elevated above us **[v2]**; elevation cached per
     PID+process start time **[v5]**).
   - Publish `ProbeResult{seq, root, ok}` via `Volatile.Write` of an immutable object.
2. **LBUTTONDOWN #2** → `Second(seq)`; if latest probe has the same seq and `ok` (and, without Shift, the window is
   resizable or maximizable — a fixed-size dialog keeps native behaviour **[v4]**) → **swallow** (return 1), set
   "swallow matching LBUTTONUP", sample Shift (`GetAsyncKeyState`), post `MSG_SNAP(root, pt, shift)` to UI thread.
   Otherwise pass through (native behaviour).
3. **[v2]** After a swallow, also swallow further down/up pairs within the double-click rect and time window of
   click #1 (prevents a fast third click forming a native double-click).
4. Right/middle/X button → detector reset.
5. **Hook liveness [v2]**: reinstall on `WM_POWERBROADCAST(PBT_APMRESUMEAUTOMATIC)`, session unlock
   (`WTSRegisterSessionNotification`), display change; and when `EVENT_SYSTEM_MOVESIZESTART` fires but the hook saw no
   button-down in the last 2 s, reinstall. Reinstall requests are posted to the hook thread (SetWindowsHookEx must run
   there) **[v3]**.
6. The hook also publishes `LastButtonDownTick` (volatile) — used by settle sessions as the "user acted" signal **[v3]**.
7. Probe misses (2nd click arrived before the probe result) are counted and logged **[v3]**.

Swallowing the second press means the app never sees a double-click → no native maximize, no flicker.
The first press/release are delivered normally, so no window is left in a drag state.

### 6.2 Action (UI thread)

- No Shift: zone = leaf of the active layout of the monitor containing `pt`.
  - If the window is not maximized **[v6]** and its **actual current visual rect** matches that zone (±3 px) **[v2]** — or the tracker placed it in
    this zone and its rect still equals the final rect of that placement (move-only / min-size windows) **[v5]** →
    restore pre-snap state (a window that was maximized before the snap is re-maximized)
    (or fallback rect, N3) and **delete the window's slot** (the user un-snapped it) **[v4]**.
  - Else remember pre-snap rect (only if not currently in a zone and not maximized), snap.
- Shift: if maximized → `ShowWindowAsync(SW_RESTORE)` **[v2]**; else if it has `WS_MAXIMIZEBOX` or `WS_THICKFRAME`:
  set `rcNormalPosition` on the monitor under the cursor (if different) and `ShowWindowAsync(SW_MAXIMIZE)`.
- `MSG_SNAP` cancels any pending Shift-drag drop for that window **[v3]**.
- All window calls go through the placement worker **[v3]**.
- Each action: flash the target zone outline for ~250 ms (DragOverlay zone window) **[v2]**, update tracker & store.

### 6.3 Eligibility **[v2]**

Top-level (`GA_ROOT`), visible, not ours, not `WS_CHILD`, not `WS_EX_TOOLWINDOW` (unless `WS_EX_APPWINDOW`),
(`WS_THICKFRAME` or `WS_CAPTION`), class not in deny list (`Progman`, `WorkerW`, `Shell_TrayWnd`,
`Shell_SecondaryTrayWnd`, `#32768`, `Windows.UI.Core.CoreWindow` as root, `XamlExplorerHostIslandWindow`,
`TopLevelWindowForOverflowXamlIsland`, …), not a borderless window covering the whole monitor (games), not cloaked
**except** `DWM_CLOAKED_SHELL` (other virtual desktop) for arrange passes. For the double-click path the hit test itself is
the main gate (so frameless Qt/Electron windows that report `HTCAPTION` work). Windows without `WS_THICKFRAME` are moved
(centred in the zone) but not resized.

### 6.4 Elevated windows **[v2]**

We detect elevation of the target (`OpenProcessToken`/`TokenElevation`; access denied ⇒ treat as elevated).
If the target is elevated and we're not, we never swallow (native maximize happens) and never try to move it.
Optional tray item "Manage admin windows" (milestone 7, `Platform/AdminMode.cs`). **Implemented and hardened:**
- A UAC-elevated installer (`--install-admin-mode`) copies the exe to `%ProgramFiles%\DisplaySplit` (explicit protected
  ACL, owner Administrators, `OWNER RIGHTS` read-only) and registers an **on-demand** task (no triggers) with
  `RunLevel=HighestAvailable`, `InteractiveToken`, `ExecutionTimeLimit=PT0S`, battery settings off, `IgnoreNew`, fixed
  arguments `--admin-mode`, and a task SD that lets the user read/run but not change it. The task XML is written to the
  protected folder (not `%TEMP%`); `schtasks.exe` is run by full path.
- Autostart stays the HKCU Run key, pointing at the installed copy. Any non-elevated start finds the task, releases the
  mutex and runs it (a named event marks logon starts). A different exe only explains how to update (off → new copy →
  on); it never offers a UAC prompt itself (that would let any process phish a permanent elevation).
- The installer pins its own exe (deny write/delete) as its first action and checks PE header fields against the
  mapped image; it reads the user's data while impersonating the shell's non-elevated token.
- The elevated instance never writes HKCU (autostart toggles run `--set-autostart on|off` unelevated); tray callbacks
  are honoured only with the cursor on the icon (`Shell_NotifyIconGetRect`) or the taskbar in the foreground.
- A split-token elevated start outside admin mode ("Run as administrator") restarts unelevated.
- NativeAOT only: framework-dependent builds read `DOTNET_ROOT` / `DOTNET_STARTUP_HOOKS` from the user's environment.
- The elevated instance stores data and log in `%ProgramFiles%\DisplaySplit\data\<SID>` (user read-only), never in a
  user-writable folder. Turning the mode on imports the user's data (typed JSON only, no reparse points); turning it off
  writes a handoff marker that the next non-elevated start imports back.
- Process hardening: System32-only DLL search, image-load policy (no remote / low-IL images, prefer System32),
  RedirectionGuard. Browser/Explorer are launched with the shell's non-elevated token (`CreateProcessWithTokenW`),
  failing closed. **[v6]** Single-instance: `CreateMutex` returning
`ERROR_ACCESS_DENIED` (elevated owner) counts as "already running"; the host window allows the open-editor message from
lower integrity via `ChangeWindowMessageFilterEx`.

## 7. Snap engine **[v3 rewritten]**

All calls below execute on the **placement worker** (§3); the UI thread only enqueues jobs and receives results.

```
Place(hwnd, visualTarget):
  if IsZoomed(hwnd) || IsWindowArranged(hwnd) || IsIconic(hwnd):
      insets = cachedRestoredInsets(hwnd) ?? default (7,0,7,7 scaled by window DPI)
      wp.rcNormalPosition = ScreenToWorkspace(visualTarget + insets)
      wp.showCmd = keepMinimized ? SW_SHOWMINNOACTIVE : SW_SHOWNOACTIVATE      [v3: never steal focus]
      SetWindowPlacement(hwnd, wp)                  // restores + places in one step, caption never off-screen
  else:
      insets = DWMWA_EXTENDED_FRAME_BOUNDS vs GetWindowRect (cached per hwnd)
      SetWindowPos(hwnd, 0, visualTarget + insets, NOZORDER|NOACTIVATE|NOOWNERZORDER|ASYNCWINDOWPOS)
  SetWindowPlacement flags [v4]: Zone slot → clear WPF_RESTORETOMAXIMIZED; Maximized slot kept minimized → set it.
  Verify [v3/v4]: UI-thread polling every 100 ms while a placement is in flight (no WinEvent hook needed, zero idle
      cost; skipped for windows kept minimized):
      re-read DWM bounds; if within 2 px → done.
      once the rect has changed and is stable for 2 polls:
        if the window's monitor DPI differs from before → recompute insets, re-place once   [v4: checked first]
        else if larger than the target → min-size limit → centre in zone (once)
        else → recompute insets, re-place once
      no change by 1.5 s (app hasn't applied it / ignored it) → one retry, then give up.
```

- Process is Per-Monitor-V2 aware → physical pixels everywhere. Workspace↔screen conversion uses the target monitor's
  `rcWork - rcMonitor` offset.
- Our own moves are recorded (`hwnd → expected rect, until`) so the tracker/settle logic ignores resulting events.

## 8. Window tracking & persistence (R6) **[v2 rewritten]**

### 8.1 App identity

`AppIdentity.Key` (string), first match wins:
1. Window AUMID: `SHGetPropertyStoreForWindow` → `PKEY_AppUserModel_ID` (Chrome/Edge profiles & PWAs, many apps).
2. Packaged process: `GetApplicationUserModelId(process)` (UWP/MSIX). For `ApplicationFrameWindow` use the hosted
   `Windows.UI.Core.CoreWindow` child's process (resolved lazily, may appear later).
3. Normalised exe path: lower-case, `\app-1.2.3\` segments removed (Squirrel), `WindowsApps\Name_1.2.3_x64__hash\` →
   `WindowsApps\Name__hash\`, `%LOCALAPPDATA%`-style prefixes kept as-is.
Then `Key = "<id>|<windowClass>"`. Excluded apps (settings) are ignored entirely.
**[v3]** A window whose identity resolves to `ApplicationFrameHost.exe` (CoreWindow not attached yet) is *unresolved*:
it is retried during its settle window (on NAMECHANGE/UNCLOAKED/250 ms timer, max 5 s) and is never stored or matched
under the frame-host key.

### 8.2 Slots

```
PlacementsFile.Apps[Key] = List<Slot>  (max 8, LRU by LastUsedUtc)
Slot { Id, Kind: Zone|Maximized, MonitorKey, LayoutId, ZoneId, Fraction (zone rect / work area), Title (normalised),
       LastUsedUtc, ExplicitZones: { layoutId → zoneId }, DerivedZones: { layoutId → zoneId } }     [v5/v6]
```
**[v5/v6] Per-layout assignments.** Each slot (and each tracked window) keeps two maps `layoutId → zoneId`:
`Explicit` (zones the user chose while that layout was active) and `Derived` (computed by overlap). An explicit
placement (snap, user move into a zone) sets `Explicit[active] = zone`, `LayoutId/ZoneId/Fraction`, and **clears all
`Derived` entries** (they were computed from the old position); explicit entries of other layouts are kept. Resolving for
the active layout: `Explicit[active]` → `Derived[active]` → derive by overlap with `Fraction` into `Derived` (a stale id
whose zone no longer exists is dropped and re-derived). So Focus → Full → Focus restores the exact arrangement, and a
window explicitly moved later follows its new position into layouts where the user never placed it.

Normalised title: strip common app suffix (" - Visual Studio Code", " — Mozilla Firefox", " - Google Chrome", …,
generically: the last " - " / " — " segment when ≥ 2 segments), collapse digits in parentheses "(3)" (unread counters).

Create/update a slot when: DisplaySplit snaps a window to a zone or maximizes it via Shift+double-click; and when the
user ends a move/size (`MOVESIZEEND` not caused by us) and the window's rect matches a zone (±4 px).
`MOVESIZEEND` is **ignored when the window's rect and zoomed state are unchanged since `MOVESIZESTART`** (a plain
caption click also produces the pair) **[v4]**; a window that is zoomed at `MOVESIZEEND` is judged as *Maximized*
(slot kept/updated), never as "out of zone" **[v4]**.
Remove the window's slot **only** when **[v3]**:
- the user ends a real move/size (`MOVESIZEEND` not caused by us, rect changed) and the rect matches no zone, or
- the user un-snaps it with a double-click (N3) **[v4]**, or
- a window tracked as *Maximized* is found restored (not zoomed, not minimized) at a `SYSTEM_FOREGROUND` check, with no
  settle session active (via `GetWindowPlacement` on the worker; minimized windows are never judged by `GetWindowRect`).
Closing or minimizing a window keeps its slot. Events produced by our own placements are ignored (§7).

### 8.3 Restore policy (pure `RestorePolicy`, unit tested)

Candidate = eligible window becomes visible (`EVENT_OBJECT_SHOW` or `EVENT_OBJECT_UNCLOAKED`), key has slots,
RememberPositions on, not excluded, and:
- this hwnd was never seen before (apply **at most once per hwnd**). **[v4/v5]** On a *manual* start every existing
  top-level window (hidden and cloaked included) is recorded as seen, so tray apps re-showing old windows and
  virtual-desktop switches (`UNCLOAKED` storms) never count as new. With `--autostart` only the windows the arrange pass
  considered (visible, minimized or shell-cloaked) are recorded as seen; still-hidden windows of apps launching at logon
  (Slack/Teams/Discord start hidden or in the tray) go through the normal first-show path. A window is recorded as seen
  only once it is eligible (an OBJECT_SHOW while still app-cloaked doesn't consume it); and
- (process started < 60 s ago) **or** (no other live window with the same key exists), and
- no mouse button is down and no move/size loop is active, and
- window is resizable and not much smaller than a typical main window (skip if < 400×300 physical — splash/login).

Slot choice (`SlotMatcher`): among slots not occupied by another live window of the same key, best title score
(exact normalised match > prefix/contains > none), ties → most recent. Re-evaluated once when the title changes within the
settle period (`EVENT_OBJECT_NAMECHANGE` hook filtered to the settling process) if a strictly better free slot exists.

Apply: Zone slot → `Place` (via SetWindowPlacement path for zoomed/minimized; minimized windows get `rcNormalPosition`
only and stay minimized). Maximized slot **[v4]** → if already maximized on the target monitor: nothing. Minimized: set
`rcNormalPosition` on the target monitor + `WPF_RESTORETOMAXIMIZED`, stay minimized. Otherwise `SetWindowPlacement`
with `SW_SHOWMAXIMIZED` (arrange runs before the user starts working). **[v6]** No attempt is made to re-activate the previous foreground
window afterwards (the foreground lock would usually refuse it); the focus shift at logon is accepted.

Settle session per handled window (max 10 s):
- `EVENT_OBJECT_LOCATIONCHANGE` hook installed per settling **process** (`idProcess` filter), removed when its last
  session ends.
- If the window moves away from the target and the change wasn't ours and the user didn't act → re-apply (max 3).
  If the app maximizes itself after one re-apply → accept.
- Session ends immediately on user action on that window: `MOVESIZESTART`, `MINIMIZESTART`, or a button-down (the LL
  hook's `LastButtonDownTick`) while the cursor is over that window. Keyboard input is **not** treated as user action
  **[v3]** (GetLastInputInfo counts mouse moves); a Win+Arrow in the first seconds may be re-applied once — acceptable.

### 8.4 WinEvent hooks (out-of-context, `SKIPOWNPROCESS`, each event registered individually)

Permanent: `OBJECT_SHOW`, `OBJECT_UNCLOAKED`, `SYSTEM_MOVESIZESTART`, `SYSTEM_MOVESIZEEND`,
`SYSTEM_MINIMIZESTART`, `SYSTEM_FOREGROUND`. Callbacks filter `idObject == OBJID_WINDOW && idChild == 0` first and return
fast. Destroy notifications come from `RegisterShellHookWindow` (`HSHELL_WINDOWDESTROYED`) plus lazy `IsWindow` checks.
Per-process during settle: `OBJECT_LOCATIONCHANGE`, `OBJECT_NAMECHANGE`.

### 8.5 Startup arrange

- Autostart launches `DisplaySplit.exe --autostart`. With `--autostart` and ArrangeOnLogon (**default on**): apply slots
  to all existing eligible windows (incl. shell-cloaked), one at a time through the placement worker — then normal restore
  policy for new windows. **[v3]** No logon-time query needed: `--autostart` only happens at logon, so every window that
  exists then was opened in this session.
- Manual start/restart: no auto-arrange (tray "Arrange all windows now" exists).
- Autostart mechanism: HKCU Run key (default; reliable, no admin). Elevated mode → scheduled task (§6.4).

### 8.6 Monitor identity

`MonitorKey` = EDID-based: `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME)` →
`edidManufactureId:edidProductCodeId:connectorInstance` (+ friendly name for UI); also store monitor device path & size.
Matching tiers when loading layouts/slots: exact key → device path → same manufacturer/product → "exactly one monitor
connected and exactly one unmatched stored layout" → else new default layout.
Display changes are debounced 2 s; slots for missing monitors are kept; windows queued for a missing monitor are retried on
the next display change (within their settle window).

### 8.7 Storage

`%APPDATA%\DisplaySplit\{settings,layouts,placements}.json` (`DISPLAYSPLIT_HOME` override for tests).
Write: `x.tmp` → `Flush(true)` → copy current to `x.bak` → `File.Move(tmp, x, overwrite: true)`. Load: parse `x`, on failure
parse `x.bak`, on failure use defaults **and do not overwrite** the corrupt file until the user changes something
(corrupt file renamed `x.corrupt-<timestamp>`). Debounced 1 s on a thread-pool timer; flushed synchronously on Exit and
`WM_ENDSESSION`.

## 9. Shift-drag (N1) **[v3 rewritten]**

`MOVESIZESTART` → record start rect & cursor; start a 16 ms timer only during the loop. The overlay is **armed** only when
all of these hold: Shift is down; the window's *position* moved ≥ `SM_CXDRAG`/`SM_CYDRAG` from its start rect with its
size unchanged (a real move — a plain caption press also produces MOVESIZESTART/END, and resizes change size). The
movement threshold already excludes double-clicks, so no "double-click pending" delay is applied **[v4]**. Once armed:
DragOverlay on
the cursor's monitor — one LWA_ALPHA window per zone (`WS_EX_NOACTIVATE|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_LAYERED|
WS_EX_TOPMOST`, `SW_SHOWNA`), zone under cursor filled with accent colour; releasing Shift hides it (disarms).
`MOVESIZEEND` while armed with a highlighted zone → snap on next tick, **unless the window didn't move or a `MSG_SNAP`
arrived** for it. Note in README: Windows' own edge-snap may also trigger at screen edges.

## 10. Tray & settings

Tray icon (runtime-drawn). Left-click → editor. Right-click menu:

```
Edit layout…                        Win+Alt+Z
Layout ▸  saved layouts (✓ active) ─ New from preset ▸ …     (per-monitor submenus when > 1 monitor)
Arrange all windows now
Don't remember "<last foreground app>"
Excluded apps ▸   (click an entry to include it again)          [v3]
Forget all remembered positions…  (confirmation dialog)          [v3]
──────────
✓ Double-click title bar to snap
✓ Shift-drag to snap
✓ Remember app positions
✓ Start with Windows
──────────
Pause
Exit
```

Switching the active layout (tray, hotkey or editor Save) refits zone-tracked windows of that monitor using their
per-layout assignment for the new layout (derived by overlap only when none exists) and resolves stored slots the same
way **[v4/v5]** — explicit assignments of other layouts are kept, so switching back restores the exact arrangement.

## 11. Performance budget

- Idle CPU ≈ 0 % (no timers when idle; LL hook wakes on mouse events only and returns immediately on moves).
- Working set 20–30 MB idle (R2R). Editor open: + DIB (~42 MB at 5120×2088) — released on close.
- Hook callback: no allocation, no blocking, < 50 µs.

## 12. Testing

- Unit (xUnit): layout ops & invariants (exact tiling, id stability, remove/merge, normalize), presets, resolve with gaps,
  detector (time, distance, wrap-around, third click), SlotMatcher & RestorePolicy decisions, AppIdentity path
  normalisation, title normalisation, JSON round-trip & corrupt-file recovery, struct sizes, editor hit-testing math.
- E2E (interactive desktop only, takes over the mouse for ~2 s): start DisplaySplit with temp `DISPLAYSPLIT_HOME` and a
  halves layout; start a Win32 test window; SendInput double-click on caption → assert rect == zone; Shift variant →
  `IsZoomed`; restart target → reappears in zone; target that moves itself 500 ms after showing → settles back in zone;
  reboot path: start target windows first, then `DisplaySplit --autostart` → windows arranged **[v3]**;
  Shift+single-click on caption → window unchanged **[v3]**.
- Manual matrix (documented checklist): Notepad, Explorer, Chrome/Edge, VS Code, Terminal, Office, Settings (UWP),
  a WPF app, a DPI-unaware app. Includes verifying that Win11 Explorer / XAML-island title bars start a move loop on press
  (the [v6] root-fallback evidence); if not, those apps safely keep native maximize.

## 13. Milestones

1. Skeleton: csproj, manifest, interop, hidden window, tray, single instance, storage.
2. Core + unit tests.
3. MonitorService, placement worker, SnapEngine, MouseHook + probe (R2, R3) + the double-click E2E **[v3]**.
4. Editor overlay (R1, R4).
5. Identity, tracker, persistence, restore policy (R6).
6. Shift-drag, hotkeys, autostart, first-run (N1, N2, R7).
7. Elevated mode (optional), remaining E2E, README.
