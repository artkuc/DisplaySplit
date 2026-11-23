# Security Policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Report them privately with GitHub's
[private vulnerability reporting](https://github.com/artkuc/DisplaySplit/security/advisories/new).

Please include:

- the affected version;
- a description of the issue and its impact;
- steps to reproduce.

You can expect an initial response within a week.

## Scope

DisplaySplit is a local desktop utility:

- It makes no network connections and collects no telemetry.
- It runs with your normal user rights unless you turn on *Manage admin windows*. If it is started with *Run as
  administrator* (UAC on), it restarts itself without administrator rights.
- It stores its settings in `%APPDATA%\DisplaySplit`.

### Admin mode (*Manage admin windows*)

When this is turned on, DisplaySplit runs with administrator rights without a UAC prompt. The design goal is that
turning it on creates **no new way to gain administrator rights** for anything running as you without them:

- The elevated exe is a copy in `%ProgramFiles%\DisplaySplit` with an explicit ACL: only SYSTEM and Administrators can
  write, and `OWNER RIGHTS` stops the owner from changing the ACL.
- It is started by an on-demand scheduled task (`RunLevel=HighestAvailable`, no stored password) whose own ACL lets you
  run it but not change it. Its command line is fixed (`--admin-mode`). Creating or updating the copy and the task
  always needs a UAC prompt.
- Only the self-contained NativeAOT build can turn it on. A framework-dependent build loads the .NET runtime and startup
  hooks from user-controlled environment variables (`DOTNET_ROOT`, `DOTNET_STARTUP_HOOKS`).
- The elevated process keeps its data and log in `%ProgramFiles%\DisplaySplit\data\<SID>` and never writes into a
  folder you can modify, so it can't be redirected with symlinks, junctions or hard links. `DISPLAYSPLIT_HOME` is ignored.
  It doesn't write to your registry hive either: autostart changes go through a short-lived non-elevated helper.
- When the mode is turned on, your existing settings are read **while impersonating your normal token**, so a link
  can't make the installer read anything you couldn't read yourself. Only typed settings data is copied, never raw files.
- It loads DLLs from System32 only (`/DEPENDENTLOADFLAG`, `SetDefaultDllDirectories`), blocks remote and
  low-integrity images, and doesn't follow junctions created by non-admins (RedirectionGuard, Windows 11 22H2 and
  later). The task's command line rejects `%` so Task Scheduler can't expand variables into it.
- The browser or Explorer windows it opens, and its restart when you turn the mode off, are started with your normal
  (non-elevated) token. If that isn't possible, nothing is started.
- Non-elevated processes can only send it two messages: "open the editor" and tray-icon clicks. Tray clicks are acted
  on only when the cursor is on the icon or the taskbar has the keyboard, so another program can't pop the menu under
  your cursor.
- DisplaySplit never offers to elevate on its own initiative. UAC prompts come only from you ticking the tray item.

Known limits:

- The UAC prompt you approve when turning the mode on runs the exe you started. The installer pins that file
  immediately (no rename or replace while it runs) and checks it against the running image, but if something replaced
  the exe *before* you started it, approving the prompt installs that. This is the same as with any program you elevate.
  The exe isn't code-signed yet; signing would let the installer verify the publisher.
- Another program running as you can create DisplaySplit's single-instance mutex first and so keep DisplaySplit from
  starting (denial of service only; this was already possible without admin mode).

Reports of any way around these guarantees are very welcome (see above).

The settings include remembered window titles. Treat `log.txt` as potentially sensitive when you share it.
