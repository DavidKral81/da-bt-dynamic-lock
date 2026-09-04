# Changelog

What changed between releases, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/).

The version itself lives in `windows/version.py` — one constant the app, the
installer, both `.exe` resources and the APK all read. Change it there and
nowhere else.

## Unreleased

### Fixed

- **The app now knows the screen is locked for as long as it is locked.** The
  check asked whether the secure desktop was in front, which is true only while
  the lock screen is being drawn — cross-checked against the Winlogon log on
  29 Aug 2026, the session was locked for 10 h 40 min and the app saw it for
  one second of that. Everything built on it was therefore running blind: the
  pause introduced in 1.4 lasted a second, and the screen went on being
  "locked" again through the night. Windows is now asked for the session's own
  lock state (`WTSQuerySessionInformation`), with the old desktop test kept as
  a fallback for when that call fails.

- **A lock the radio cannot corroborate is held off.** The scanner on Windows
  goes progressively deaf, and the silence it produces looks exactly like a
  phone that walked away — both end with a locked screen. Before locking, the
  app now checks what the radio hears from *any* device: when that is nothing
  at all, the lock waits, the scanner is restarted and the silence is measured
  again. Bounded to two hold-offs in a row, so a genuinely quiet room cannot
  switch the guard off. Measured on 29 Aug 2026: a lock landed with the phone
  on the desk while the radio heard 3 advertisements from 1 device in 15 s,
  where the honest locks that night had 29–37 from 4–5 devices.

### Changed

- **The countdown appears on every monitor**, one box per screen, each centred
  on its own and at the height set in the settings. It used to be drawn on the
  primary monitor only, so anyone working on a second screen got no warning at
  all before the lock. Whoever prefers the old behaviour can switch on *Show
  the countdown on the primary monitor only*, right below the position in the
  settings (`countdown_primary_only` in `config.json`). The log now names each
  box — `Countdown box 1/2 at …` — so it stays possible to tell which screen a
  box landed on.

- **The chart's time axis shows the time of day** (`09:45`, `09:46`, …) instead
  of the distance from now (`−1 min`, `−2 min`). On the 2 minute range the
  labels carry seconds as well. The grid lines moved with it: they now sit on
  whole minutes or hours of the clock, where they used to sit on a random
  second, because they were anchored to the time since the computer booted.

## 1.4 — 25 Aug 2026

### Fixed

- **The app no longer counts down and locks behind the lock screen.** Once the
  screen was locked, a single advertisement from the phone started the whole
  cycle again: after the delay a countdown box was drawn where the lock screen
  hid it, and then the screen was "locked" a second time, which does nothing.
  Between 19 and 22 Aug 2026 that accounted for 41 of the 68 locks recorded in
  the log. Watching now pauses while the screen is locked, and the delay starts
  from zero when it is unlocked — so finishing your password is never followed
  by an immediate lock.
- **A failed lock is no longer reported as a success.** `LockWorkStation` can
  return an error, and it was never looked at: the log said "Locking", the app
  settled down to wait for the phone, and the screen stayed wide open. A
  failure is now written down, the tray says so, and the next tick tries again.
- **A broken log can no longer stop the app.** Writing to the log was
  unguarded, and the line that schedules the next tick sat where an error
  skipped it — one failed write and the app would sit in the tray watching
  nothing, for ever. Log rotation failing is reported once instead of silently.
- Settings that cannot be written are reported instead of passing unnoticed —
  the switch used to move while the file did not, and the old value came back
  at the next start.
- Turning start-at-logon on or off now verifies that it really happened rather
  than trusting the return code of `schtasks`, and says what went wrong when it
  did not. The installer's own "could not create the task" report can finally
  fire; the app always handed it a success code before.
- A damaged `history.json` no longer stops the app from starting. Only the JSON
  parsing was guarded, so a file that parsed but held the wrong shape raised
  while it was being read out, before any window appeared.
- Settings that could not be read were announced with `print`, which goes
  nowhere in a windowed app. The message now waits and reaches the log.
- A scanner that refuses to stop, a desktop size Windows would not report and a
  failed idle-time reading are all written to the log now. Each of them used to
  be swallowed, and the last two silently substituted a made-up value.

### Added

- When the countdown box appears, the log records where it actually landed, how
  big it is, whether Windows considers it visible and what window is in front
  of it. Until now the log only proved that a countdown had been *decided on*,
  which is not the same as anyone seeing it.
- The log can now tell a phone that went quiet from a radio that stopped
  listening. Both look identical from the outside — silence, then a lock — so
  the countdown starting and the scanner watchdog restarting both now say how
  many advertisements the radio picked up, and from how many devices. A
  countdown beginning next to `radio heard 0 advertisements from 0 devices`
  is a lock nobody earned; one beginning next to a hundred means the phone
  really did fall silent.

### Changed

- The link that leads to the phone app now says "for the phone" rather than
  "into the phone", in the app and the installer alike — it opens a download
  page, it does not put anything into the phone by itself.

## 1.3 — 20 Aug 2026

Versions 1.1 and 1.2 were never published. 1.1 was tagged and left behind when
the release was postponed; 1.2 was built only so the wake-up fix could be tried
on a real machine, and the two log corrections found during that test made it
obsolete. Everything they would have contained is in this release, so that no
two different builds ever carry the same number.

### Added

- The tray menu has a **Settings** item, above Quit. It opens the window on
  the settings tab — and unlike the chart item it never closes the window,
  only brings it to the front.
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
- The log no longer reports good news it cannot know. After the measurement
  restarts, the clock reads zero, and that used to be announced as "Phone is
  advertising again" about a phone that had said nothing at all. A signal
  strength that was deliberately forgotten now reads `unknown` instead of
  `None dBm`, which looked like a measurement.
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
