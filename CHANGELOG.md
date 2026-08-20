# Changelog

What changed between releases, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/).

The version itself lives in `windows/version.py` — one constant the app, the
installer, both `.exe` resources and the APK all read. Change it there and
nowhere else.

## Unreleased

Version 1.1 was tagged but never published — no release, no downloads. What
was going to be in it is listed here and goes out in the next release
instead, so that no two different builds ever carry the same number.

### Added

- The countdown window's height on the screen is now a setting, in tenths from
  the top (30 % by default). Horizontally it stays centred, on the primary
  monitor.
- The installer says which version it is installing, in its first window and
  in the one that reports the result. Uninstalling shows the version that is
  being removed.
- Both apps now show their version next to a link to the releases page, so
  updating no longer needs a cable: on the phone the browser downloads the APK
  and installs it over the existing app. Neither app talks to the network by
  itself — the link is handed to the browser, and you compare the version you
  see with the one on the page. The phone app still has no `INTERNET`
  permission.

### Fixed

- Waking the computer no longer locks the screen straight away. While the
  machine slept nothing was being measured, so the app woke up seeing a long
  silence and locked at once — with the phone lying on the desk, and with no
  countdown, because the countdown window had been skipped over entirely. The
  measurement now starts again after a gap, and guarding continues: a phone
  that really is gone still locks the screen after the usual delay.
- A decision to lock is checked once more immediately before it is carried
  out. A Windows notification could sit in between and take several seconds,
  long enough for the phone to come back — the log then read `Locking
  (silence 0 s)`, a lock nobody had earned.

### Changed

- The language is switched with flags in the top right corner — in the app,
  the installer and the uninstaller alike. The drop-down in the settings is
  gone: one setting, one control. A flag also stays readable for someone who
  installed the app in a language they cannot read.

## 1.0 — 18 Aug 2026

First public release.

### Added

- Locks Windows when the watched Bluetooth LE device goes out of range, and a
  companion Android app that keeps the phone broadcasting.
- Live chart of the signal strength, from 2 minutes up to a whole day.
- Countdown window before locking, an idle guard, a temporary pause and a
  warning when the device stops being heard at all.
- Czech and English throughout, including the installer and the phone app.
- One program in both roles, installer and uninstaller; autostart through a
  Task Scheduler task the app creates itself.
