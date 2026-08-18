# Design notes

Why the app works the way it does. Every number here was measured, not
guessed — the thresholds in the app are derived from these runs.

## The measurements (14–15 Aug 2026)

Windows laptop, Android phone, ordinary flat and office. Samples counted over
five-minute windows.

| Situation | Signals/min | Longest gap | RSSI |
|---|---|---|---|
| Phone without the app, idle | **1.6** | 244 s | — |
| Phone without the app, screen on | 192 | — | — |
| With the broadcasting app, on the desk | 177 | 3 s | −73 dBm |
| With the app, in a pocket, BT mouse off | 84 | 9 s | −79 dBm |
| With the app, in a pocket, BT mouse on | 20 | **20 s** | −83 dBm |

What follows from it:

- **Android barely advertises on its own.** 1.6 signals per minute with gaps
  up to four minutes is far too little to decide anything — hence the phone
  app. This is the single reason the project has two parts.
- **A Bluetooth mouse costs about 4× the samples.** One radio is shared, so
  the scanner hears the phone far less often while the mouse is active. The
  default silence threshold (45 s) has to survive that.
- **A pocket costs about 10 dB.**
- **Distance cannot be measured.** At the desk −81 dBm, three metres away
  −83 dBm — a smaller difference than the natural fluctuation of the signal.
  The app works only because a wall cuts the pocket-strength signal off
  completely, so the room boundary doubles as the signal boundary.

## Decisions

**It cannot run as a Windows service.** Services live in session 0 with no
access to the desktop, and `LockWorkStation` from there locks nothing. A
scheduled task running as SYSTEM has the same problem. The app therefore runs
as a normal user process with a tray icon.

**The logon task is created from XML, not with `schtasks /Create /SC ONLOGON`.**
The plain command cannot express "restart after failure" and, more
importantly, cannot remove the execution time limit after which Windows kills
the task on its own (3 days by default).

**The application creates that task, not the installer** (`--autostart-on`).
The tray menu offers the same switch; two pieces of code doing the same thing
would drift apart — one setting the restart policy and the other not.

**The phone broadcasts as a non-connectable beacon.** The first version was
connectable and also sent the name in the scan response. The laptop then
received two packets instead of one (427 samples/min instead of 205) and the
signal strength was twice as jittery (8.5 dB spread instead of 5.2) with dips
to −110 dBm, because strength is measured unreliably on a scan response.

**The chart merges samples per pixel, and bucket boundaries are anchored to
absolute time.** Without merging, a day range meant hundreds of thousands of
canvas objects per refresh — the window froze and ate hundreds of MB. Without
the anchoring, boundaries shifted on every refresh, samples hopped between
neighbouring buckets and the curve flickered.

**The signal history is thinned.** The last hour is kept in full, anything
older only every ~10 s. A day at full resolution is ~150 000 samples for no
visible gain.

**The scanner restarts periodically.** Freshly started, its longest gap was
8.9 s; after hours of running, gaps of 30–200 s appeared at the same signal
strength. A long-running scanner on Windows gradually goes deaf. The restart
is cheap (~0.5 s). It is also rate-limited: a restart cannot conjure up a
signal that is not there, and without a brake an absent phone caused 47 000
restarts overnight.

**Pinning to the taskbar is not possible.** Verified by listing the shell
verbs of both the shortcut and the executable on Windows 11 build 26200 — the
verb does not exist; Microsoft blocked it so installers cannot help
themselves to the taskbar.

## Limits worth knowing

- **It can only lock, never unlock.** Windows does not let third-party
  programs unlock a session.
- **The watched device is chosen by name.** An unnamed device cannot be
  offered meaningfully, and phone MAC addresses rotate, so the name from the
  advertisement is what identifies it.
- **No target picked = nothing is watched.** An empty target used to match
  every device around, which meant any nearby BLE gadget kept the screen
  unlocked.
- **The installer is not code-signed**, so SmartScreen warns on first run.
