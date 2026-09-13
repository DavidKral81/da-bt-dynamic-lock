using System.Text.Json;
using System.Text.Json.Serialization;
using DaBtDynamicLock.Core;

namespace DaBtDynamicLock.Engine;

/// <summary>
/// Reads and writes the chart's history.
///
/// The file keeps WALL CLOCK times, while the history itself works in monotonic
/// seconds - seconds since the machine started, which do not jump when the clock
/// is adjusted or the machine sleeps. Converting between the two is the whole
/// job here, and it is why this is not just "serialise the object".
///
/// The file format is the one 1.5 writes, field for field, so a history written
/// by either version still reads in the other.
/// </summary>
public static class HistoryStore
{
    /// <summary>
    /// A gap shorter than this between the last write and this start is not
    /// worth marking as "the app was not running" - it is just the time between
    /// the last save and a restart.
    /// </summary>
    private const double ShortestGapWorthMarking = 5;

    private sealed record Stored
    {
        [JsonPropertyName("until")] public double Until { get; init; }
        [JsonPropertyName("samples")] public List<double[]>? Samples { get; init; }
        [JsonPropertyName("locks")] public List<double>? Locks { get; init; }
        [JsonPropertyName("downtime")] public List<double[]>? Downtime { get; init; }
    }

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };

    /// <summary>Writes the history out. Returns what went wrong, or null.</summary>
    public static string? Save(SignalHistory history, string path,
        double nowMono, double nowWall)
    {
        try
        {
            // Thinned before writing, not after reading: the point is to keep
            // the FILE small, and a file written unthinned is already several
            // megabytes by the time anyone notices.
            history.Thin(nowMono);

            var stored = new Stored
            {
                Until = Math.Round(nowWall, 2),
                Samples = history.Samples()
                    .Select(s => new[] { Wall(s.At, nowMono, nowWall), s.Rssi })
                    .ToList(),
                Locks = history.Locks()
                    .Select(t => Wall(t, nowMono, nowWall)).ToList(),
                Downtime = history.Downtimes()
                    .Select(d => new[]
                    {
                        Wall(d.From, nowMono, nowWall),
                        Wall(d.To, nowMono, nowWall),
                    })
                    .ToList(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(stored, Format));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the chart history could not be saved ({e.Message})";
        }
    }

    /// <summary>
    /// Loads the previous run's history and marks the stretch the app was not
    /// running. Returns how long that stretch was, in minutes, or null.
    /// </summary>
    /// <remarks>
    /// The WHOLE read is guarded, not only the JSON parsing. Valid JSON with the
    /// wrong shape inside - a sample that is not a pair, a string where a number
    /// belongs - used to throw while it was being unpacked, outside the guard,
    /// and 1.5 then did not start at all. A lost chart is worth losing; a
    /// program that will not run is not.
    /// </remarks>
    public static (double? NotRunningMinutes, string? Problem) Load(SignalHistory history,
        string path, double nowMono, double nowWall)
    {
        if (!File.Exists(path))
            return (null, null);

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is null)
                return (null, "the chart history was empty");

            double oldest = nowWall - SignalHistory.LengthSeconds;

            var samples = (stored.Samples ?? new())
                .Where(pair => pair.Length == 2 && pair[0] >= oldest)
                .Select(pair => new Sample(Mono(pair[0], nowMono, nowWall), (int)pair[1]))
                .ToList();

            var locks = (stored.Locks ?? new())
                .Where(t => t >= oldest)
                .Select(t => Mono(t, nowMono, nowWall))
                .ToList();

            var downtimes = (stored.Downtime ?? new())
                .Where(pair => pair.Length == 2 && pair[1] >= oldest)
                .Select(pair => new Downtime(Mono(pair[0], nowMono, nowWall),
                    Mono(pair[1], nowMono, nowWall)))
                .ToList();

            // Between the last write and this start the app was not running -
            // drawn as a gap rather than left as a suspiciously flat stretch.
            double? notRunning = null;
            double gap = nowWall - stored.Until;
            if (stored.Until > 0 && gap > ShortestGapWorthMarking)
            {
                downtimes.Add(new Downtime(Mono(stored.Until, nowMono, nowWall), nowMono));
                notRunning = gap / 60;
            }

            history.Restore(samples, locks, downtimes);
            return (notRunning, null);
        }
        catch (Exception e) when (e is IOException or JsonException
                                    or UnauthorizedAccessException
                                    or InvalidCastException or OverflowException)
        {
            return (null, $"the chart history is unreadable ({e.Message}) - starting empty");
        }
    }

    /// <summary>Monotonic second to wall clock.</summary>
    private static double Wall(double at, double nowMono, double nowWall) =>
        Math.Round(nowWall - (nowMono - at), 2);

    /// <summary>Wall clock back to a monotonic second of THIS run.</summary>
    private static double Mono(double at, double nowMono, double nowWall) =>
        nowMono - (nowWall - at);
}
