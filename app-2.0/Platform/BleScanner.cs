using DaBtDynamicLock.Core;
using Windows.Devices.Bluetooth.Advertisement;

namespace DaBtDynamicLock.Platform;

/// <summary>How the scanner is run. The numbers come from measurement, see below.</summary>
public sealed record ScannerSettings
{
    /// <summary>
    /// How long one scanning session lasts before it is restarted. Measured
    /// 15.08.2026: a freshly started scanner's longest gap was 8.9 s, whereas
    /// after hours of running gaps of 30-200 s showed up at the same signal
    /// strength. A long-running scanner on Windows goes progressively deaf, and
    /// a restart costs about half a second.
    /// </summary>
    public double RestartAfterSeconds { get; init; } = 120;

    /// <summary>
    /// Silence after which the scanner is brought back without waiting for the
    /// period. Doubles after every futile attempt - see the loop.
    /// </summary>
    public double WatchdogSeconds { get; init; } = 45;

    /// <summary>What the watched device's target string is right now.</summary>
    public required Func<string> Target { get; init; }

    /// <summary>The watching settings, read fresh - they can change at runtime.</summary>
    public required Func<WatchSettings> Watch { get; init; }
}

/// <summary>
/// Listens for BLE advertisements and feeds them to <see cref="PhoneWatch"/>.
///
/// Nothing is paired with and nothing is connected to - advertisements are
/// listened to passively. Which is also why ANY advertising BLE device can be
/// watched, not only a phone running the companion app.
///
/// Through WinRT's BluetoothLEAdvertisementWatcher, which is the same interface
/// the Python version reaches through bleak - so this is not new ground, it is
/// the same radio with one layer fewer.
/// </summary>
public sealed class BleScanner
{
    /// <summary>
    /// -127 means "RSSI unknown", not a measurement. Anything at or below -120
    /// is treated as noise rather than a reading.
    /// </summary>
    private const int UnusableRssi = -120;

    private readonly PhoneWatch _watch;
    private readonly ScannerSettings _cfg;
    private readonly Action<string> _log;
    private readonly Func<double> _now;

    /// <summary>
    /// Set when the main loop is about to lock on a silence the radio cannot
    /// corroborate: the scanner starts over so a fresh one gets a full threshold
    /// to hear the device. Not throttled like the watchdog, because it can only
    /// happen as often as a lock is held off.
    /// </summary>
    private volatile bool _restartRequested;

    public BleScanner(PhoneWatch watch, ScannerSettings cfg, Action<string> log,
        Func<double>? now = null)
    {
        _watch = watch;
        _cfg = cfg;
        _log = log;
        _now = now ?? PhoneWatch.MonotonicSeconds;
    }

    /// <summary>Ask for a restart at the next opportunity.</summary>
    public void RequestRestart() => _restartRequested = true;

    /// <summary>
    /// Runs until cancelled: start, listen, stop, start again.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        bool firstRun = true;
        int futile = 0;     // restarts in a row that brought nothing

        while (!stopping.IsCancellationRequested)
        {
            BluetoothLEAdvertisementWatcher? watcher = null;
            double sessionStart = _now();
            try
            {
                watcher = new BluetoothLEAdvertisementWatcher
                {
                    // Passive is enough and costs less: the watched device
                    // advertises as a non-connectable beacon and puts its name
                    // in the advertisement itself, so there is no scan response
                    // to ask for. Measured 13.09.2026 over 10 s - passive heard
                    // 60 advertisements from the phone, active 35.
                    ScanningMode = BluetoothLEScanningMode.Passive,
                };
                watcher.Received += OnAdvertisement;
                watcher.Stopped += OnStopped;
                watcher.Start();

                if (firstRun)
                {
                    _log("Scanner started.");
                    firstRun = false;
                }

                futile = await ListenAsync(sessionStart, futile, stopping);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop - not a failure, and not worth a log line.
            }
            catch (Exception e)
            {
                _log($"Scanner failed ({e.Message}) - trying again in 10 s.");
                await Delay(10, stopping);
            }
            finally
            {
                // The scanner MUST be stopped even after an exception. Left
                // running without an owner it keeps consuming advertisements,
                // and repeated attempts pile them up: on 15.08.2026 that made
                // one phone report a hundred times over, 450 000 samples, and
                // froze the chart.
                if (watcher is not null)
                {
                    try
                    {
                        watcher.Received -= OnAdvertisement;
                        watcher.Stopped -= OnStopped;
                        watcher.Stop();
                    }
                    catch (Exception e)
                    {
                        // Never swallowed: a stop() that does not stop is
                        // exactly what produces the pile-up described above.
                        _log($"The scanner could not be stopped ({e.Message}) - it may "
                            + "keep consuming advertisements in the background.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// One scanning session: waits out the period, unless the silence or a
    /// request cuts it short. Returns how many futile restarts stand in a row.
    /// </summary>
    private async Task<int> ListenAsync(double sessionStart, int futile,
        CancellationToken stopping)
    {
        while (_now() - sessionStart < _cfg.RestartAfterSeconds)
        {
            await Delay(1, stopping);
            if (stopping.IsCancellationRequested)
                break;

            if (_restartRequested)
            {
                _restartRequested = false;
                break;
            }

            // A restart cannot conjure up a signal. When the device is simply
            // gone the condition below stays true, so without a brake the
            // scanner would restart every second for ever - on 15.08.2026 that
            // produced 47 000 restarts overnight and as many log lines. Hence
            // the doubling, capped at ten minutes.
            double wait = Math.Min(_cfg.WatchdogSeconds * Math.Pow(2, Math.Min(futile, 4)), 600);
            // Asked of the last advertisement, not of the silence: with a
            // sensitivity threshold set, a device that is heard but too weak
            // means the radio is working and there is nothing to restart.
            if (_watch.SinceSeen() >= wait && _now() - sessionStart > wait)
            {
                futile++;
                var (adverts, devices) = _watch.HeardRecently(PhoneWatch.HeardWindowSeconds);
                _log($"No signal for {wait:F0} s - restarting the scanner "
                    + $"(futile attempt no. {futile}); radio heard {adverts} "
                    + $"advertisements from {devices} devices in the last "
                    + $"{PhoneWatch.HeardWindowSeconds:F0} s.");
                break;
            }
        }

        // A session that heard the device at all was not futile, even if it
        // ended on the watchdog - the next dry spell starts with a full
        // allowance again.
        if (_watch.SinceSeen() is double ago && _now() - ago > sessionStart)
            futile = 0;
        return futile;
    }

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs e)
    {
        int rssi = e.RawSignalStrengthInDBm;
        if (rssi <= UnusableRssi)
            return;

        // Counted before anything else and for every device: this is the proof
        // that the radio is still listening at all.
        string address = FormatAddress(e.BluetoothAddress);
        _watch.RecordHeard(address);

        string? name = e.Advertisement.LocalName;
        // The target is read on every advertisement rather than once at the
        // start, because it can be switched at runtime.
        if (DeviceMatch.Matches(address, name, ServiceUuids(e), _cfg.Target()))
        {
            var note = _watch.Record(rssi, _cfg.Watch());
            if (note.What == Sighting.FirstSeen)
                _log($"Phone seen for the first time ({note.Rssi} dBm) - watching.");
            else if (note.What == Sighting.BackAtTheDesk)
                _log($"Phone back at the desk after {note.SilenceSeconds:F0} s "
                    + $"of silence ({note.Rssi} dBm).");
        }

        _watch.RecordNearby(name, rssi);
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs e)
    {
        // Windows stops the watcher on its own when the radio goes away (turned
        // off, adapter removed). Saying so beats a scanner that is quietly not
        // scanning - the loop starts a new one on its next round anyway.
        if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
            _log($"The radio stopped the scanner ({e.Error}).");
    }

    /// <summary>
    /// WinRT hands the address over as a number; the rest of the app speaks
    /// "AA:BB:CC:DD:EE:FF", which is also what a target typed by hand looks like.
    /// </summary>
    public static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        // Little-endian, and only the low six bytes are the address.
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    private static IEnumerable<string> ServiceUuids(
        BluetoothLEAdvertisementReceivedEventArgs e) =>
        e.Advertisement.ServiceUuids.Select(u => u.ToString());

    /// <summary>
    /// A wait that ends quietly when the app is closing rather than throwing
    /// into the loop above.
    /// </summary>
    private static async Task Delay(double seconds, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stopping);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
