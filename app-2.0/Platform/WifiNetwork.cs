using System.Runtime.InteropServices;
using System.Text;

namespace DaBtDynamicLock.Platform;

/// <summary>The wireless network in use: its name and the access point's address.</summary>
/// <param name="Ssid">Network name as broadcast.</param>
/// <param name="Bssid">MAC of the access point, "AA:BB:CC:DD:EE:FF".</param>
public sealed record WifiConnection(string Ssid, string Bssid);

/// <summary>
/// Which wireless network the machine is on.
///
/// Both halves matter. A network is matched by name AND by the access point's
/// address, because a name alone is trivially faked - call a hotspot after the
/// user's home network and watching switches itself off. This setting turns
/// protection OFF, so a mistake here costs security, not convenience. (The
/// price: a mesh needs every access point saved separately, and a new router
/// means saving the network again.)
///
/// Read through wlanapi, NOT by parsing "netsh wlan show interfaces", whose
/// output is localised - protection that quietly stops recognising home after a
/// language change is worse than none.
///
/// And NOT through WinRT either, which was the obvious thing to try: measured
/// 13.09.2026, Windows.Networking.Connectivity gives the SSID and nothing else.
/// The whole of WlanConnectionProfileDetails is GetConnectedSsid - no BSSID at
/// any version. So this P/Invoke stays.
/// </summary>
public static class WifiNetwork
{
    private const uint WlanClientVersion = 2;
    private const int WlanOpcodeCurrentConnection = 7;
    private const int WlanInterfaceConnected = 1;
    private const int Dot11SsidMaxLength = 32;

    /// <summary>
    /// The network in use, or null for: no wireless stack, no adapter, not
    /// connected, or a failed query. All four answer the caller's question the
    /// same way, so it does not have to tell them apart - but a failure still
    /// comes back as a Problem to be logged, rather than as silence.
    ///
    /// A network that cannot be read is not trusted, so any failure can only
    /// lead to MORE locking.
    /// </summary>
    public static Reading<WifiConnection?> Current()
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        try
        {
            uint version = 0;
            uint rc = WlanOpenHandle(WlanClientVersion, IntPtr.Zero, ref version, ref handle);
            if (rc != 0)
                return Failed($"WlanOpenHandle failed (code {rc})");

            rc = WlanEnumInterfaces(handle, IntPtr.Zero, ref list);
            if (rc != 0)
                return Failed($"WlanEnumInterfaces failed (code {rc})");

            // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then the array.
            int count = Marshal.ReadInt32(list);
            IntPtr items = list + 8;
            int stride = Marshal.SizeOf<WlanInterfaceInfo>();

            for (int i = 0; i < count; i++)
            {
                var iface = Marshal.PtrToStructure<WlanInterfaceInfo>(items + i * stride);
                if (iface.isState != WlanInterfaceConnected)
                    continue;
                var found = ConnectionOf(handle, iface.InterfaceGuid);
                if (found.Value is not null || !found.Ok)
                    return found;
            }
            return new Reading<WifiConnection?>(null);   // adapter there, not connected
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// What one connected adapter is associated with. Split off so each query's
    /// memory is freed on its own path.
    /// </summary>
    private static Reading<WifiConnection?> ConnectionOf(IntPtr handle, Guid guid)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint rc = WlanQueryInterface(handle, ref guid, WlanOpcodeCurrentConnection,
                IntPtr.Zero, out uint size, ref data, IntPtr.Zero);
            if (rc != 0 || data == IntPtr.Zero)
                return Failed($"WlanQueryInterface failed (code {rc})");

            // Exactly, not "at least". A structure declared four bytes short
            // still fits inside what Windows returns, so "at least" lets a
            // shifted layout through - measured 13.09.2026 by dropping
            // dot11BssType: every check downstream still passed and the BSSID
            // was quietly read from the wrong offset. A size that does not
            // match is the only cheap proof that the fields line up.
            int expected = Marshal.SizeOf<WlanConnectionAttributes>();
            if (size != expected)
                return Failed($"WlanQueryInterface returned {size} B, expected exactly "
                    + $"{expected} - the structure is not laid out as declared");

            var assoc = Marshal.PtrToStructure<WlanConnectionAttributes>(data)
                               .wlanAssociationAttributes;

            // Cheap proof that the structure is laid out as declared: quality is
            // a percentage. A shifted field lands outside 0-100 almost every
            // time, which is the difference between noticing and silently
            // trusting the wrong network.
            if (assoc.wlanSignalQuality > 100)
                return Failed($"signal quality {assoc.wlanSignalQuality} is out of range "
                    + "- the structure is not laid out as declared");

            int length = Math.Min((int)assoc.dot11Ssid.uSSIDLength, Dot11SsidMaxLength);
            string ssid = DecodeSsid(assoc.dot11Ssid.ucSSID, length);
            if (ssid.Length == 0)
                return new Reading<WifiConnection?>(null);

            string bssid = string.Join(":", assoc.dot11Bssid.Select(b => b.ToString("X2")));
            return new Reading<WifiConnection?>(new WifiConnection(ssid, bssid));
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
        }
    }

    /// <summary>
    /// A network name that is not valid UTF-8 is still a name. It must not
    /// crash anything, and it must not quietly become a DIFFERENT name either:
    /// two networks whose names both decoded to the replacement character would
    /// compare equal, and being on one would switch off locking for the other.
    /// So undecodable bytes are kept as escapes and compare consistently.
    /// </summary>
    private static string DecodeSsid(byte[] raw, int length)
    {
        var strict = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback,
                                          DecoderFallback.ExceptionFallback);
        try
        {
            return strict.GetString(raw, 0, length);
        }
        catch (DecoderFallbackException)
        {
            var text = new StringBuilder();
            for (int i = 0; i < length; i++)
                text.Append(raw[i] < 0x80 ? ((char)raw[i]).ToString() : $"\\x{raw[i]:x2}");
            return text.ToString();
        }
    }

    private static Reading<WifiConnection?> Failed(string problem) =>
        Reading<WifiConnection?>.Failed(null, problem + " - treating this as an unknown network");

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint client, IntPtr reserved,
        ref uint negotiatedVersion, ref IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved,
        ref IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode,
        IntPtr reserved, out uint dataSize, ref IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public WlanAssociationAttributes wlanAssociationAttributes;
        public WlanSecurityAttributes wlanSecurityAttributes;
    }
}
