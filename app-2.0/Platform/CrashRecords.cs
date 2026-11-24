using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DaBtDynamicLock.Platform;

/// <summary>One crash of a program, as Windows wrote it down (Application Error 1000).</summary>
/// <param name="ProcessCreated">When the crashed process started (FILETIME,
/// UTC). With the process id it names one run.</param>
/// <param name="ReportId">Windows' id of the crash report; empty when it gave none.</param>
public sealed record CrashRecord(
    DateTime TimeUtc,
    string AppPath,
    string AppVersion,
    string ModuleName,
    string ModuleVersion,
    uint ExceptionCode,
    int ProcessId,
    long ProcessCreated,
    string ReportId);

/// <summary>
/// The crashes Windows wrote to its Application log for one program, signed in
/// as the current user.
///
/// Read through wevtapi rather than System.Diagnostics.EventLog, which is a
/// separate package for this kind of app - no dependency to download before
/// anything can be checked. The fields are read from the event's DATA, never
/// from its message text: the message is in the language of Windows, the data
/// is not (the same lesson as netsh and the Wi-Fi name).
///
/// Only this program's records are ever rendered: the query names the program
/// and the user, so nothing of other programs passes through here.
/// </summary>
public static class CrashRecords
{
    /// <param name="programName">The executable's file name, e.g. DaBtDynamicLock.exe.</param>
    public static Reading<IReadOnlyList<CrashRecord>> Since(DateTime sinceUtc, string programName)
    {
        var none = Array.Empty<CrashRecord>();

        // The name goes into the query, so it is checked rather than escaped:
        // ours is plain letters, and anything else is refused, not guessed at.
        if (!Regex.IsMatch(programName, @"^[A-Za-z0-9._-]{1,100}$"))
            return Reading<IReadOnlyList<CrashRecord>>.Failed(none,
                $"\"{programName}\" is not a plain file name - not put into an event log query");

        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            return Reading<IReadOnlyList<CrashRecord>>.Failed(none,
                "the current user's SID is unknown");

        string since = sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
        string query =
            "*[System[Provider[@Name='Application Error'] and EventID=1000"
            + $" and TimeCreated[@SystemTime>='{since}']"
            + $" and Security[@UserID='{sid}']]"
            + $" and EventData[Data[@Name='AppName']='{programName}']]";

        nint results = EvtQuery(0, "Application", query, EvtQueryChannelPath);
        if (results == 0)
            return Reading<IReadOnlyList<CrashRecord>>.Failed(none,
                $"EvtQuery failed (error {Marshal.GetLastWin32Error()})");

        var found = new List<CrashRecord>();
        var problems = new List<string>();
        try
        {
            var handles = new nint[16];
            while (true)
            {
                if (!EvtNext(results, handles.Length, handles, 2000, 0, out int returned))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    return Reading<IReadOnlyList<CrashRecord>>.Failed(found,
                        $"EvtNext failed (error {error})");
                }

                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        string? xml = Render(handles[i], out int renderError);
                        if (xml is null)
                            problems.Add($"EvtRender failed (error {renderError})");
                        else if (Parse(xml) is CrashRecord record)
                            found.Add(record);
                        else
                            problems.Add("a record without the expected fields");
                    }
                    finally
                    {
                        EvtClose(handles[i]);
                    }
                }
            }
        }
        finally
        {
            EvtClose(results);
        }

        // What could be read is kept; what could not is said, once.
        return problems.Count == 0
            ? new Reading<IReadOnlyList<CrashRecord>>(found)
            : Reading<IReadOnlyList<CrashRecord>>.Failed(found,
                $"{problems.Count} record(s) could not be read: {problems[0]}");
    }

    /// <summary>
    /// The event as XML. Asked twice: once for the size, once for the text -
    /// the way the API is meant to be used.
    /// </summary>
    private static string? Render(nint handle, out int error)
    {
        error = 0;
        if (EvtRender(0, handle, EvtRenderEventXml, 0, 0, out int used, out _))
            return "";      // nothing to render - cannot happen for an event
        error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
            return null;

        nint buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!EvtRender(0, handle, EvtRenderEventXml, used, buffer, out _, out _))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            error = 0;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The fields of one Application Error 1000 record, or null when one that
    /// matters is missing or not in the expected form. Public so the tests can
    /// feed it a record without Windows.
    /// </summary>
    public static CrashRecord? Parse(string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        XNamespace ns = root.Name.Namespace;
        string? time = root.Element(ns + "System")?.Element(ns + "TimeCreated")
            ?.Attribute("SystemTime")?.Value;
        var data = root.Element(ns + "EventData")?.Elements(ns + "Data")
            .Where(d => d.Attribute("Name") is not null)
            .GroupBy(d => d.Attribute("Name")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Value) ?? new();

        string Field(string name) => data.TryGetValue(name, out var v) ? v : "";

        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            || !uint.TryParse(Field("ExceptionCode"), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint code)
            || !TryHex(Field("ProcessId"), out long pid)
            || string.IsNullOrEmpty(Field("AppPath")))
            return null;

        // The start time is only needed when there is no report id; a record
        // without it is still a crash worth telling.
        TryHex(Field("ProcessCreationTime"), out long created);

        return new CrashRecord(at, Field("AppPath"), Field("AppVersion"),
            Field("ModuleName"), Field("ModuleVersion"), code, (int)pid, created,
            Field("IntegratorReportId"));
    }

    /// <summary>Windows writes these as "0x2ee4".</summary>
    private static bool TryHex(string text, out long value)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? text[2..] : text;
        return long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out value);
    }

    private const int EvtQueryChannelPath = 0x1;
    private const int EvtRenderEventXml = 1;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtQuery(nint session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(nint resultSet, int eventArraySize,
        [Out] nint[] eventArray, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(nint context, nint fragment, int flags,
        int bufferSize, nint buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint handle);
}
