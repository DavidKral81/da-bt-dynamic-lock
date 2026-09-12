using System.Text.Json;
using System.Text.Json.Serialization;
using DaBtDynamicLock.Core;

namespace DaBtDynamicLock.Engine;

/// <summary>One saved wireless network where the screen is not locked.</summary>
public sealed record TrustedNetwork
{
    [JsonPropertyName("ssid")] public string Ssid { get; init; } = "";

    /// <summary>
    /// The access point's address. Saved alongside the name because a name on
    /// its own is trivially forged - and this setting switches protection OFF.
    /// </summary>
    [JsonPropertyName("bssid")] public string Bssid { get; init; } = "";
}

/// <summary>
/// Everything the user can set. Read from and written to config.json, keeping
/// the same key names the shipped 1.5 uses, so a settings file written by
/// either version still reads.
/// </summary>
public sealed record Settings
{
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("silence_s")] public double SilenceSeconds { get; set; } = 45;
    [JsonPropertyName("active")] public bool Active { get; set; } = true;
    [JsonPropertyName("countdown")] public bool Countdown { get; set; } = true;
    [JsonPropertyName("countdown_from_s")] public int CountdownFromSeconds { get; set; } = 15;
    [JsonPropertyName("countdown_vertical")] public double CountdownVertical { get; set; } = 0.30;
    [JsonPropertyName("countdown_primary_only")] public bool CountdownPrimaryOnly { get; set; }
    [JsonPropertyName("rssi_threshold")] public double? RssiThreshold { get; set; }
    [JsonPropertyName("threshold_window_s")] public double ThresholdWindowSeconds { get; set; } = 6;
    [JsonPropertyName("idle_guard")] public bool IdleGuard { get; set; }
    [JsonPropertyName("idle_guard_s")] public double IdleGuardSeconds { get; set; } = 15;
    [JsonPropertyName("trusted_network_pause")] public bool TrustedNetworkPause { get; set; }

    [JsonPropertyName("trusted_networks")]
    public List<TrustedNetwork> TrustedNetworks { get; set; } = new();

    [JsonPropertyName("scanner_restart_s")] public double ScannerRestartSeconds { get; set; } = 120;
    [JsonPropertyName("silence_watchdog_s")] public double SilenceWatchdogSeconds { get; set; } = 45;
    [JsonPropertyName("alert_no_signal_min")] public double AlertNoSignalMinutes { get; set; } = 10;
    [JsonPropertyName("language")] public string Language { get; set; } = "cs";
    [JsonPropertyName("log")] public bool Log { get; set; } = true;

    /// <summary>
    /// Keys this version does not know: the "_name" lines that document the
    /// file for whoever opens it, and anything a newer version might add.
    /// Kept so that saving does not quietly throw them away - a settings file
    /// that loses its own explanations after the first click is worse than one
    /// that was never commented.
    /// </summary>
    /// <remarks>
    /// JsonElement and not JsonNode: the serializer only accepts the former
    /// here and says so at RUN time, not while compiling.
    /// </remarks>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();

    /// <summary>The subset the decision logic reads.</summary>
    public WatchSettings ForWatching() => new()
    {
        Active = Active,
        SilenceSeconds = SilenceSeconds,
        Countdown = Countdown,
        CountdownFromSeconds = CountdownFromSeconds,
        IdleGuard = IdleGuard,
        IdleGuardSeconds = IdleGuardSeconds,
        RssiThreshold = RssiThreshold,
        ThresholdWindowSeconds = ThresholdWindowSeconds,
    };

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // Czech network names and device names must survive a round trip as
        // themselves, not as í escapes - the file is meant to be readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Settings from disk, or the defaults. A missing or damaged file must
    /// never stop the app: a fresh install has no file at all, and a
    /// half-written one should not be fatal either. What went wrong comes back
    /// as a problem to be logged once the log exists - it cannot be logged from
    /// here, because whether to log at all is one of these settings.
    /// </summary>
    public static (Settings Value, string? Problem) Load(string path)
    {
        if (!File.Exists(path))
            return (new Settings(), null);
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            return loaded is null
                ? (new Settings(), $"{Path.GetFileName(path)} was empty - using the defaults")
                : (loaded, null);
        }
        catch (Exception e) when (e is IOException or JsonException
                                    or UnauthorizedAccessException)
        {
            return (new Settings(),
                $"{Path.GetFileName(path)} is unreadable ({e.Message}) - using the defaults");
        }
    }

    /// <summary>
    /// Writes the settings out. Called straight after the user flips something,
    /// so a failed write must be reported: in 1.4 it passed unnoticed, the
    /// switch moved, the file did not, and the setting was back to its old
    /// value after a restart.
    /// </summary>
    public string? Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the settings could not be saved ({e.Message})";
        }
    }
}
