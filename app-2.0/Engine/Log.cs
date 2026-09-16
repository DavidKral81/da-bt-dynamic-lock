namespace DaBtDynamicLock.Engine;

/// <summary>
/// The log. One line per event, English only - it is what faults get diagnosed
/// from and what gets attached to a bug report, so it must read the same
/// whatever language the interface is in.
///
/// Never throws. An exception escaping from here used to travel up through the
/// main loop and stop it from ever being scheduled again: the app then sat in
/// the tray looking alive and watched nothing.
///
/// What must NOT go in: the network name and the access point's address. Logs
/// get sent when something is reported, and somebody else's router MAC is not
/// ours to pass on.
/// </summary>
public sealed class Log
{
    /// <summary>Bytes after which the file is rotated. The same 2 MB as 1.5.</summary>
    public const long MaxBytes = 2_000_000;

    private readonly string _path;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    /// <summary>Rotation is complained about once, not on every line.</summary>
    private bool _rotateFailed;

    public Log(string path, Func<bool> enabled, Func<DateTime>? clock = null)
    {
        _path = path;
        _enabled = enabled;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Set once the folder the log lives in has been removed on purpose.</summary>
    private volatile bool _fileStopped;

    /// <summary>
    /// From now on lines go to the console only. For the uninstaller after it
    /// has removed the settings folder: writing on would make the folder again,
    /// with a log in it, right after it was asked to be gone.
    /// </summary>
    public void StopWritingFile() => _fileStopped = true;

    /// <summary>Writes one line. Swallows nothing silently, raises nothing either.</summary>
    public void Write(string message)
    {
        string line = $"{_clock():dd.MM.yyyy HH:mm:ss}  {message}";
        Console.WriteLine(line);        // visible when run from a console
        if (!_enabled() || _fileStopped)
            return;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report to - the log IS the reporting channel.
                // Deliberate, and the only suppression here: the caller must
                // carry on, and a console line is better than nothing.
                Console.WriteLine($"  (this line could not be written to the log: {e.Message})");
            }
        }
    }

    /// <summary>
    /// Starts a new file once the old one is big enough, keeping one previous
    /// file. A failure is reported INTO THE LOG rather than to a console
    /// nobody reads - and only once, or a full disk would fill what is left of
    /// it with complaints.
    /// </summary>
    private void Rotate()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes)
            return;
        try
        {
            File.Move(_path, _path + ".1", overwrite: true);
            _rotateFailed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_rotateFailed)
                return;
            _rotateFailed = true;
            File.AppendAllText(_path,
                $"{_clock():dd.MM.yyyy HH:mm:ss}  The log could not be rotated "
                + $"({e.Message}) - it will keep growing past {MaxBytes / 1_000_000} MB."
                + Environment.NewLine);
        }
    }
}
