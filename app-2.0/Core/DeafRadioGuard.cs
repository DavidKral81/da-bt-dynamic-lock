namespace DaBtDynamicLock.Core;

/// <summary>What to do with a lock whose silence the radio cannot vouch for.</summary>
public enum DeafRadioVerdict
{
    /// <summary>The radio hears the room, so the silence is the device's. Lock.</summary>
    RadioHearsTheRoom,
    /// <summary>Silence belongs to the scanner. Hold the lock off, restart the scanner.</summary>
    HoldOff,
    /// <summary>Held off as often as allowed - the room may really be empty. Lock.</summary>
    LockAnyway,
}

/// <summary>
/// Tells a scanner gone deaf apart from a device that walked away.
///
/// From outside the two look identical: the device stops being heard and the
/// screen locks. What separates them is everything ELSE the radio hears - a
/// deaf scanner hears nothing from anyone. Measured 29.08.2026: a lock landed
/// with the phone on the desk while the radio heard 3 advertisements from 1
/// device, where the honest locks that night had 29-37 from 4-5.
///
/// Only the decision and the counter live here. Restarting the scanner,
/// restarting the measurement and writing the log line are the caller's, so
/// this stays testable over plain numbers.
/// </summary>
public sealed class DeafRadioGuard
{
    /// <summary>Below this many advertisements in the window...</summary>
    public const int DeafAdverts = 5;

    /// <summary>...and below this many devices, the radio is deaf.</summary>
    public const int DeafDevices = 2;

    /// <summary>
    /// How many locks in a row may be held off. Not unlimited: a room really can
    /// be empty and quiet, and a guard that silence can switch off is no guard
    /// at all. Each hold-off costs one more silence threshold.
    /// </summary>
    public const int HoldOffMax = 2;

    private int _holdOffs;

    /// <summary>Hold-offs used in a row, for the log line the caller writes.</summary>
    public int HoldOffsInARow => _holdOffs;

    /// <summary>
    /// Asked just before locking, with what the radio heard in the last
    /// <see cref="PhoneWatch.HeardWindowSeconds"/> seconds.
    /// </summary>
    public DeafRadioVerdict Judge(int adverts, int devices)
    {
        if (adverts >= DeafAdverts || devices >= DeafDevices)
        {
            // The radio is fine, so the device really is gone. The count is
            // cleared as well, so two unrelated deaf spells never share one
            // allowance.
            _holdOffs = 0;
            return DeafRadioVerdict.RadioHearsTheRoom;
        }

        if (_holdOffs >= HoldOffMax)
        {
            _holdOffs = 0;
            return DeafRadioVerdict.LockAnyway;
        }

        _holdOffs++;
        return DeafRadioVerdict.HoldOff;
    }
}
