using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persists the last unhandled crash two ways, so it can be recovered without `adb` (this app is
/// normally sideloaded onto a single personal phone, where logcat isn't practically available):
///
/// 1. SharedPreferences, read back by MainActivity's "Last crash" card - but this only helps if
///    the app gets far enough to actually show that screen.
/// 2. A plain text file at a fixed, well-known path
///    (Android/data/uk.co.accessuk.scalebridge/files/crash_log.txt on the phone's internal
///    storage), which needs no permissions to write (it's this app's own app-specific external
///    storage directory) and can be read by plugging the phone into a PC over USB (exposed via
///    MTP) even when the app itself cannot render any UI at all - see
///    <see cref="ScaleBridgeApplication"/>, which additionally tries to launch a standalone,
///    dependency-free crash screen directly.
/// </summary>
public static class CrashLog
{
    private const string PrefsName = "scale_bridge_crash";
    private const string KeyText = "last_crash_text";
    private const string KeyUtcTicks = "last_crash_utc_ticks";
    public const string CrashFileName = "crash_log.txt";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static void Record(Context context, Exception exception)
    {
        string text = exception.ToString();

        try
        {
            using var editor = Prefs(context).Edit()!;
            editor.PutString(KeyText, text);
            editor.PutLong(KeyUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            // Commit() (synchronous) rather than Apply(): this often runs moments before the
            // process is torn down by the crash, so the write must complete immediately.
            editor.Commit();
        }
        catch
        {
            // Never let the crash-logging path itself throw from inside an exception handler.
        }

        try
        {
            var dir = context.GetExternalFilesDir(null);
            if (dir is not null)
            {
                string path = System.IO.Path.Combine(dir.AbsolutePath, CrashFileName);
                System.IO.File.WriteAllText(path, $"{DateTimeOffset.UtcNow:u}\n\n{text}\n");
            }
        }
        catch
        {
            // As above - this is a best-effort secondary copy, never let it throw.
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
