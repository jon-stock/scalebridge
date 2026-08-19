using Android.Content;

namespace ScaleBridge.Ble;

/// <summary>
/// Tracks when <see cref="ScaleScanReceiver"/> last actually acted on a scan match, as a
/// deliberate backstop on top of <see cref="ScaleScanRegistrar"/>'s
/// <c>ScanCallbackType.FirstMatch</c> setting: some scales keep advertising periodically even
/// while idle/not being stood on, which previously produced a fresh "please step on the
/// scale"/"sync failed" notification (and a real GATT connection attempt, foreground service
/// start, and battery draw on both phone and scale) roughly once a minute indefinitely. Persisted
/// to <see cref="SharedPreferences"/> (not an in-memory field) since <see cref="ScaleScanReceiver"/>
/// can run in a freshly-started process for each broadcast.
/// </summary>
internal static class ScanCooldownStore
{
    private const string PrefsName = "scale_bridge_scan_cooldown";
    private const string KeyLastAttemptUtcTicks = "last_attempt_utc_ticks";

    // A scale genuinely being stepped on twice within this window (e.g. two people weighing back
    // to back) will simply be silently ignored the second time - an acceptable tradeoff for a
    // single-user app, versus repeated background wake-ups/notifications for a scale that's just
    // sitting there still advertising.
    public static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(5);

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static bool IsInCooldown(Context context)
    {
        long ticks = Prefs(context).GetLong(KeyLastAttemptUtcTicks, 0);
        if (ticks == 0)
            return false;

        var last = new DateTimeOffset(ticks, TimeSpan.Zero);
        return DateTimeOffset.UtcNow - last < CooldownPeriod;
    }

    public static void RecordAttempt(Context context, DateTimeOffset whenUtc)
    {
        using var editor = Prefs(context).Edit()!;
        editor.PutLong(KeyLastAttemptUtcTicks, whenUtc.UtcTicks);
        editor.Apply();
    }
}
