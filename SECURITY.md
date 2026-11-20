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
- It runs with your normal user rights, unless you start it elevated yourself.
- It stores its settings in `%APPDATA%\DisplaySplit`.

The settings include remembered window titles. Treat `log.txt` as potentially sensitive when you share it.
