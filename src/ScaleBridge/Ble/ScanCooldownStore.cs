using Android.Content;

namespace ScaleBridge.Ble;

/// <summary>
/// Tracks how long <see cref="ScaleScanReceiver"/> should ignore further scan matches, as a
/// deliberate backstop on top of <see cref="ScaleScanRegistrar"/>'s
/// <c>ScanCallbackType.FirstMatch</c> setting: some scales keep advertising periodically even
/// while idle/not being stood on, which previously produced a fresh "please step on the
/// scale"/"sync failed" notification (and a real GATT connection attempt, foreground service
/// start, and battery draw on both phone and scale) roughly once a minute indefinitely. Persisted
/// to <see cref="SharedPreferences"/> (not an in-memory field) since <see cref="ScaleScanReceiver"/>
/// can run in a freshly-started process for each broadcast.
///
/// Stores a single "cooldown until" timestamp rather than a fixed period from the last attempt,
/// because different outcomes need different cooldown lengths - see the three Record* methods
/// below. Previously this recorded a flat 5-minute cooldown the instant a scan match was
/// *accepted*, before the resulting sync's outcome was known at all. That meant a weigh-in that
/// was captured but then failed to record (e.g. a Health Connect write error) still burned the
/// full 5-minute cooldown, silently ignoring the user's obvious recovery action of stepping back
/// on the scale to trigger a fresh attempt. The cooldown is now only extended to the full 5
/// minutes for outcomes that genuinely don't need a fast retry (a completed success, or a timeout
/// where the scale was never stepped on at all); any real failure gets a much shorter cooldown so
/// "step on the scale again" actually works.
/// </summary>
internal static class ScanCooldownStore
{
    private const string PrefsName = "scale_bridge_scan_cooldown";
    private const string KeyCooldownUntilUtcTicks = "cooldown_until_utc_ticks";

    /// <summary>
    /// Applied once a sync concludes with an outcome that doesn't call for a fast retry: a fully
    /// successful weigh-in/write, or a timeout where the scale was never actually stepped on (no
    /// vendor data received at all) - the original "idle scale keeps advertising" case this store
    /// exists to guard against.
    /// </summary>
    public static readonly TimeSpan SuccessCooldownPeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Applied once a sync concludes with a real failure the user might reasonably retry by
    /// simply stepping on the scale again (a stalled/failed handshake, a disconnect before a
    /// weight was captured, or a captured weight that failed to write to Health Connect). Long
    /// enough to avoid a tight retry loop/notification spam if the same failure is just going to
    /// recur immediately regardless, but short enough that the user's obvious recovery action -
    /// step on the scale again - is not silently swallowed for the next 5 minutes.
    /// </summary>
    public static readonly TimeSpan FailureCooldownPeriod = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Set the instant a scan match is accepted and the connection service is (about to be)
    /// started, before any real outcome is known - purely to stop a duplicate/overlapping
    /// broadcast for the exact same in-flight sync from starting a second, concurrent connection
    /// attempt. Always superseded by <see cref="RecordSuccessOutcome"/> or
    /// <see cref="RecordFailureOutcome"/> once the sync concludes; only matters as a fallback if
    /// the service is killed/crashes before reaching any of its own terminal paths (see
    /// <see cref="ScaleConnectionService.OnDestroy"/>), in which case it naturally expires after
    /// this short window rather than requiring a Bluetooth toggle/reboot to recover.
    /// </summary>
    private static readonly TimeSpan InFlightCooldownPeriod = TimeSpan.FromSeconds(90);

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static bool IsInCooldown(Context context)
    {
        long ticks = Prefs(context).GetLong(KeyCooldownUntilUtcTicks, 0);
        if (ticks == 0)
            return false;

        var until = new DateTimeOffset(ticks, TimeSpan.Zero);
        return DateTimeOffset.UtcNow < until;
    }

    public static void RecordInFlight(Context context) =>
        SetCooldownUntil(context, DateTimeOffset.UtcNow + InFlightCooldownPeriod);

    public static void RecordSuccessOutcome(Context context) =>
        SetCooldownUntil(context, DateTimeOffset.UtcNow + SuccessCooldownPeriod);

    public static void RecordFailureOutcome(Context context) =>
        SetCooldownUntil(context, DateTimeOffset.UtcNow + FailureCooldownPeriod);

    private static void SetCooldownUntil(Context context, DateTimeOffset untilUtc)
    {
        using var editor = Prefs(context).Edit()!;
        editor.PutLong(KeyCooldownUntilUtcTicks, untilUtc.UtcTicks);
        editor.Apply();
    }
}
