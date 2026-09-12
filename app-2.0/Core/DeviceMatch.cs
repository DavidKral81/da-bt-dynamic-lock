namespace DaBtDynamicLock.Core;

/// <summary>Telling the watched device apart from everything else on the air.</summary>
public static class DeviceMatch
{
    /// <summary>
    /// True when this advertisement came from the watched device.
    ///
    /// ANY BLE device can be watched, not only a phone running the app: the
    /// target is compared as a substring of the advertised name, as a MAC
    /// address, or as a service UUID. Nothing is ever paired or connected to -
    /// advertisements are only listened to.
    ///
    /// The address and the name come from the advertisement, so both are the
    /// scanner's words, not ours: the address may be a rotating random one and
    /// the name may be missing altogether.
    /// </summary>
    /// <param name="address">MAC address as the scanner reports it.</param>
    /// <param name="name">Advertised local name, null when the device has none.</param>
    /// <param name="serviceUuids">Service UUIDs from the advertisement.</param>
    /// <param name="target">What the user picked - a name fragment, a MAC or a UUID.</param>
    public static bool Matches(
        string address,
        string? name,
        IEnumerable<string>? serviceUuids,
        string? target)
    {
        // No target picked yet = nothing counts. Without this an empty target
        // matched EVERYTHING - "" is a substring of every name - so any BLE
        // device around (a mouse, a TV, a neighbour's gadget) was taken for the
        // phone, and the screen stayed unlocked as long as anything at all was
        // audible. Found in use, not by the tests.
        if (string.IsNullOrEmpty(target))
            return false;

        // 17 characters with five colons is a MAC address and nothing else, so
        // it is compared whole. A name is compared as a fragment because the
        // phone advertises more than the user typed.
        if (target.Length == 17 && target.Count(ch => ch == ':') == 5)
            return string.Equals(address, target, StringComparison.OrdinalIgnoreCase);

        if (name is not null && name.Contains(target, StringComparison.OrdinalIgnoreCase))
            return true;

        return serviceUuids is not null
            && serviceUuids.Any(u => u.Contains(target, StringComparison.OrdinalIgnoreCase));
    }
}
