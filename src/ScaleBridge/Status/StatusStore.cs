using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persists the "last sync time/weight and any error state" required by Prompt.md Section 4,
/// requirement 7 (minimal status visibility). Read by MainActivity to render the basic status
/// screen; written by <see cref="Ble.ScaleConnectionService"/> at the end of every sync attempt.
/// </summary>
public static class StatusStore
{
    private const string PrefsName = "scale_bridge_status";
    private const string KeyLastSyncUtcTicks = "last_sync_utc_ticks";
    private const string KeyLastWeightKg = "last_weight_kg";
    private const string KeyLastError = "last_error";
    private const string KeyLastErrorUtcTicks = "last_error_utc_ticks";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static void RecordSuccess(Context context, double weightKg, DateTimeOffset whenUtc)
    {
        using var editor = Prefs(context).Edit()!;
        editor.PutLong(KeyLastSyncUtcTicks, whenUtc.UtcTicks);
        // SharedPreferences has no float64 API; store as the raw long bits via JavaDouble round-trip.
        editor.PutFloat(KeyLastWeightKg, (float)weightKg);
        editor.Remove(KeyLastError);
        editor.Apply();
    }

    public static void RecordError(Context context, string message, DateTimeOffset whenUtc)
    {
        using var editor = Prefs(context).Edit()!;
        editor.PutString(KeyLastError, message);
        editor.PutLong(KeyLastErrorUtcTicks, whenUtc.UtcTicks);
        editor.Apply();
    }

    public static DateTimeOffset? LastSyncUtc(Context context)
    {
        long ticks = Prefs(context).GetLong(KeyLastSyncUtcTicks, 0);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public static double? LastWeightKg(Context context)
    {
        var prefs = Prefs(context);
        if (!prefs.Contains(KeyLastWeightKg))
            return null;

        return prefs.GetFloat(KeyLastWeightKg, 0f);
    }

    public static string? LastError(Context context) => Prefs(context).GetString(KeyLastError, null);

    public static DateTimeOffset? LastErrorUtc(Context context)
    {
        long ticks = Prefs(context).GetLong(KeyLastErrorUtcTicks, 0);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
