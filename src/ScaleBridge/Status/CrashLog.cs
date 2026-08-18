using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persists the last unhandled crash so it can be read directly from the app's own screen -
/// installed via <see cref="ScaleBridgeApplication"/> as early as possible (before any Activity's
/// OnCreate runs), so this catches crashes that happen very early during startup too. Exists
/// specifically because this app is sideloaded onto a single personal device with no `adb`
/// access readily available, so logcat isn't a practical way to diagnose a crash - this makes the
/// exception text visible (and copyable, via long-press) directly in the app.
/// </summary>
public static class CrashLog
{
    private const string PrefsName = "scale_bridge_crash";
    private const string KeyText = "last_crash_text";
    private const string KeyUtcTicks = "last_crash_utc_ticks";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static void Record(Context context, Exception exception)
    {
        try
        {
            using var editor = Prefs(context).Edit()!;
            editor.PutString(KeyText, exception.ToString());
            editor.PutLong(KeyUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            // Commit() (synchronous) rather than Apply(): this often runs moments before the
            // process is torn down by the crash, so the write must complete immediately.
            editor.Commit();
        }
        catch
        {
            // Never let the crash-logging path itself throw from inside an exception handler.
        }
    }

    public static string? LastCrashText(Context context) => Prefs(context).GetString(KeyText, null);

    public static DateTimeOffset? LastCrashUtc(Context context)
    {
        long ticks = Prefs(context).GetLong(KeyUtcTicks, 0);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public static void Clear(Context context)
    {
        using var editor = Prefs(context).Edit()!;
        editor.Remove(KeyText);
        editor.Remove(KeyUtcTicks);
        editor.Apply();
    }
}
