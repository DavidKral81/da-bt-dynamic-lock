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

**The time axis is labelled with the time of day, and its grid is anchored to
the clock.** Samples are timestamped with monotonic time (seconds since the
computer booted), so a grid spaced from "now" landed on a random second — fine
while the labels only said how long ago it was, useless once they say 09:46.
The lines are therefore placed on whole minutes or hours of the local clock,
and each label is formatted from the local time of that very moment, so a
daylight saving change inside the range still labels every line correctly.

**The signal history is thinned.** The last hour is kept in full, anything
older only every ~10 s. A day at full resolution is ~150 000 samples for no
visible gain.

**The scanner restarts periodically.** Freshly started, its longest gap was
8.9 s; after hours of running, gaps of 30–200 s appeared at the same signal
strength. A long-running scanner on Windows gradually goes deaf. The restart
is cheap (~0.5 s). It is also rate-limited: a restart cannot conjure up a
signal that is not there, and without a brake an absent phone caused 47 000
restarts overnight.

Because of that deafness, "the phone stopped broadcasting" and "the laptop
stopped listening" look identical from the outside — both are silence, and
both lock the screen. So every advertisement is counted, whatever device it
came from, and the log says how many the radio picked up when a countdown
starts and when the watchdog restarts the scanner. A countdown beginning
beside `radio heard 0 advertisements from 0 devices` is a lock nobody earned;
beside a hundred, the phone really did fall silent. What prompted it: between
19 and 22 Aug 2026 the phone came back within 0–9 s of a scanner restart
thirteen times, which a restart cannot cause.

**And that count now decides, not just explains.** Once the log could tell the
two apart, the first measurement settled it: on 29 Aug 2026 the screen locked
with the phone lying on the desk while the radio was hearing 3 advertisements
from 1 device in 15 s, where the same night's honest locks had 29–37 from 4–5.
So a lock is held off when the radio hears next to nothing from anyone, the
scanner is restarted and the silence is measured afresh. The hold-off is
bounded to two in a row: a room really can be empty and quiet, and a guard that
silence can switch off is not a guard. The check lives in the loop rather than
in `decide()`, which stays pure and knows nothing about radios.

**The lock state is read from the session, not from the desktop.** The obvious
test for a plain user app is to ask for the input desktop: Windows hands the
secure one to nobody, so a refusal means the lock screen is in front. It works,
and it answers the wrong question — the secure desktop is in front only while
the lock screen is actually drawn. Cross-checked against the Winlogon log on
29 Aug 2026: the session was locked for 10 h 40 min overnight and the desktop
test reported it for one second. So `WTSQuerySessionInformation` is asked for
the session's own lock flag, which lasts as long as the lock does; the desktop
test remains as a fallback for when that call fails. The layout of the
structure Windows fills in is verified by a test, because getting the padding
wrong reads as plausible rubbish rather than as an error.

**Nothing is decided while the screen is locked.** After a lock the app waits
for the phone, and a single advertisement used to re-arm it — so behind the
lock screen the full cycle ran again: a countdown box drawn where nobody could
see it, then a second "lock" that does nothing. Cross-checking the app's log
against the Windows Winlogon events for 19–22 Aug 2026 put 41 of 68 recorded
locks in that category. Which state the session is in is answered as described
above — the session flag first, the desktop test only as a fallback. Any error
counts as "not locked", because guessing "locked" when the check itself broke
would switch the guarding off for good. On unlocking, the measurement restarts from
zero exactly as it does after a wake: the silence collected behind the lock
screen says nothing about where the phone is now, and without the restart the
screen would lock again as soon as the password was typed.

**A saved Wi-Fi network can pause the watching — and it is matched by name AND
by access point.** Wherever locking is not wanted (an office, a workshop), the
computer being connected to a chosen network suspends the guard. A network is
recognised by its name together with the MAC address of the access point,
because a name on its own is forged by naming a hotspot after it — and this
setting *switches the protection off*, so being fooled costs security rather
than convenience. Being merely in range does not count; only an actual
connection does, and that cannot be faked without the network's password. A
mesh therefore appears as one row per access point, each ticked separately,
which needs no explaining because the list shows it. The network is read
through `wlanapi` rather than by parsing `netsh wlan show interfaces`, whose
output is localised — a guard that quietly stops recognising the place after
the system language changes is worse than no guard. A failed read counts as
"not on a saved network", so a broken check can only ever lead to more
locking, never less. Neither the name nor the address is written to the log:
logs get shared when reporting a problem, and somebody else's access point is
not ours to hand out.

**The countdown appears on every monitor.** One box per screen, each centred
on its own and at the chosen height, because a warning drawn on a screen the
user is not looking at is a warning wasted. The work areas are enumerated at
every appearance, not cached — a monitor can be unplugged between two
countdowns. Whoever prefers a single box can switch the others off. Clicks
pass through the box to whatever is underneath, so a warning cannot get in
the way of the work it is warning about.

**Pinning to the taskbar is not possible.** Verified by listing the shell
verbs of both the shortcut and the executable on Windows 11 build 26200 — the
verb does not exist; Microsoft blocked it so installers cannot help
themselves to the taskbar.

**The installer is the application itself — same file, same name.** A WinUI 3
executable cannot even be renamed: it finds its own XAML through resources
keyed to its file name, so a renamed copy dies before drawing anything.
Measured before
deciding: an *empty* window in a supposedly lean toolkit came to 72.6 MB
self-contained, while the whole finished application came to 68.6 MB — those
~65 MB are .NET itself and both carry it equally. A separate installer would
therefore have meant shipping .NET twice. The download is the program, it
recognises what it is being asked to do from where it is running, and
installing means putting that one file in place.

**A logon task that repeats, because "restart on failure" does not cover a
crash.** Measured on 18 Sep 2026, after the application died: Task Scheduler
recorded the failing exit code and started nothing — that setting is about a
task that cannot be *started*. A five-minute repeat brings watching back
instead. It hangs on a time trigger, not on the logon one: a repeat on a
logon trigger only starts at the next sign-in, and the task is always created
after one — measured on 26 Sep 2026, when a killed application stayed dead
and Task Scheduler showed no next run. Quitting deliberately leaves a note
that the repeat respects, so the repeat cannot overrule the person. The note
carries the moment the user signed in, as Windows reports it, and holds only
for that sign-in: sleep and locking keep it, signing out, restarting or
shutting down end it. An earlier version keyed it to the uptime, which fast
startup and signing out do not reset — the app then stayed off the next
morning. One task per user, because task names are shared by the whole
machine and one name for everybody let users overwrite each other's.

**Do not lock while something runs full screen.** The mouse-and-keyboard
safeguard says nothing during a film, which plays for hours without a
keystroke, so a second one asks Windows whether an application is presenting
full screen (the same question Windows answers for its own notifications),
and falls back to comparing the foreground window with its monitor. Either
safeguard is enough to hold the lock off; both are off by default, since
anyone at the desk can use them to postpone locking. When one stops applying,
the silence is measured from zero again — otherwise the end of a film would
find two hours of silence and lock without a countdown, which it did before
this was fixed.

**The tray icon has to be able to come back.** Windows announces a rebuilt
notification area, and an application that does not listen for it loses its
icon for good when the shell restarts. That happened, and the failed redraws
afterwards leaked one icon each — 3305 of them in under three hours, until
there were no handles left to open a window with and the process died. The
icon is now restored on that announcement, and the clean-up happens whether
the redraw succeeded or not.

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
