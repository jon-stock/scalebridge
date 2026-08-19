using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persists weight readings that were captured from the scale but failed to write to Health
/// Connect, so a reading is never silently lost just because one sync attempt failed (e.g. the
/// Android 12+ background foreground-service-start restriction, or Health Connect being
/// temporarily unavailable - see docs/PROTOCOL_CONFIRMATION.md for both). <see cref="MainActivity"/>
/// shows whatever is pending here and lets the user retry the write manually, without needing to
/// step back on the scale to capture the same weight again.
///
/// Stored as a single delimited string rather than one SharedPreferences key per field:
/// SharedPreferences has no native "list of structs" type, and this project deliberately avoids
/// pulling in a JSON library purely to serialise a handful of (timestamp, weight) pairs - see
/// <see cref="StatusStore"/> for the same raw-primitives convention this follows. Each entry is
/// encoded as <c>"{utcTicks}|{weightKg}"</c>, entries joined with <c>;</c> - a tick count and a
/// captured weight in kg are never negative, so neither separator can collide with real data.
/// </summary>
public static class PendingSyncStore
{
    private const string PrefsName = "scale_bridge_pending_sync";
    private const string KeyEntries = "entries";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    /// <summary>One weight reading that was captured but hasn't yet been successfully written to Health Connect.</summary>
    public readonly record struct PendingReading(DateTimeOffset WhenUtc, double WeightKg);

    /// <summary>Records a reading that just failed to write to Health Connect.</summary>
    public static void Add(Context context, double weightKg, DateTimeOffset whenUtc)
    {
        var entries = GetAll(context).ToList();
        entries.Add(new PendingReading(whenUtc, weightKg));
        Save(context, entries);
    }

    /// <summary>Removes one specific reading (identified by its original capture time) once it has been successfully retried.</summary>
    public static void Remove(Context context, DateTimeOffset whenUtc)
    {
        var entries = GetAll(context).Where(e => e.WhenUtc != whenUtc).ToList();
        Save(context, entries);
    }

    /// <summary>All readings still waiting to be successfully written, oldest first.</summary>
    public static IReadOnlyList<PendingReading> GetAll(Context context)
    {
        string raw = Prefs(context).GetString(KeyEntries, "") ?? "";
        if (string.IsNullOrEmpty(raw))
            return Array.Empty<PendingReading>();

        var result = new List<PendingReading>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('|');
            if (pieces.Length != 2)
                continue; // Ignore a malformed entry rather than let it break every other one.
            if (!long.TryParse(pieces[0], out long ticks))
                continue;
            if (!double.TryParse(pieces[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double weightKg))
                continue;

            result.Add(new PendingReading(new DateTimeOffset(ticks, TimeSpan.Zero), weightKg));
        }

        return result;
    }

    private static void Save(Context context, List<PendingReading> entries)
    {
        string raw = string.Join(';', entries.Select(e =>
            $"{e.WhenUtc.UtcTicks}|{e.WeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        using var editor = Prefs(context).Edit()!;
        editor.PutString(KeyEntries, raw);
        editor.Apply();
    }
}
