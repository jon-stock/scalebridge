using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persists every weight reading this app has ever captured from the scale, successful or not -
/// what <see cref="MainActivity"/> shows as the scrollable "History" list (Prompt.md's "very
/// basic screen" grew this once the app was past its initial MVP stage). Replaces the earlier,
/// narrower <c>PendingSyncStore</c> (which only tracked failed readings) with one unified store:
/// every capture is recorded here immediately, marked <see cref="Entry.Synced"/> once its Health
/// Connect write actually succeeds - so a reading that fails and is later retried successfully
/// just becomes an ordinary synced history row, rather than needing to move between two separate
/// stores.
///
/// Stored as a single delimited string rather than one SharedPreferences key per field:
/// SharedPreferences has no native "list of structs" type, and this project deliberately avoids
/// pulling in a JSON library purely to serialise a list of (timestamp, weight, synced) tuples -
/// see <see cref="StatusStore"/> for the same raw-primitives convention this follows. Each entry
/// is encoded as <c>"{utcTicks}|{weightKg}|{syncedFlag}"</c>, entries joined with <c>;</c> - a
/// tick count and a captured weight in kg are never negative and the synced flag is always
/// exactly "0" or "1", so none of the separators can collide with real data.
/// </summary>
public static class WeightHistoryStore
{
    private const string PrefsName = "scale_bridge_weight_history";
    private const string KeyEntries = "entries";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    /// <summary>One weight reading captured from the scale, and whether it has been successfully written to Health Connect yet.</summary>
    public readonly record struct Entry(DateTimeOffset WhenUtc, double WeightKg, bool Synced);

    /// <summary>Records a reading that was just captured and successfully written to Health Connect.</summary>
    public static void RecordSynced(Context context, double weightKg, DateTimeOffset whenUtc) =>
        Upsert(context, whenUtc, weightKg, synced: true);

    /// <summary>Records a reading that was captured but failed to write to Health Connect - shown with a "pending retry" affordance until <see cref="MarkSynced"/> is called for it.</summary>
    public static void RecordPending(Context context, double weightKg, DateTimeOffset whenUtc) =>
        Upsert(context, whenUtc, weightKg, synced: false);

    /// <summary>Marks a previously-pending reading (identified by its original capture time) as successfully synced, once a retry succeeds.</summary>
    public static void MarkSynced(Context context, DateTimeOffset whenUtc) =>
        Upsert(context, whenUtc, weightKg: null, synced: true);

    /// <summary>Every reading still waiting to be successfully written.</summary>
    public static IReadOnlyList<Entry> GetPending(Context context) =>
        GetAll(context).Where(e => !e.Synced).ToList();

    /// <summary>Every captured reading, newest first.</summary>
    public static IReadOnlyList<Entry> GetAll(Context context)
    {
        string raw = Prefs(context).GetString(KeyEntries, "") ?? "";
        if (string.IsNullOrEmpty(raw))
            return Array.Empty<Entry>();

        var result = new List<Entry>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('|');
            if (pieces.Length != 3)
                continue; // Ignore a malformed entry rather than let it break every other one.
            if (!long.TryParse(pieces[0], out long ticks))
                continue;
            if (!double.TryParse(pieces[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double weightKg))
                continue;

            bool synced = pieces[2] == "1";
            result.Add(new Entry(new DateTimeOffset(ticks, TimeSpan.Zero), weightKg, synced));
        }

        result.Sort((a, b) => b.WhenUtc.CompareTo(a.WhenUtc));
        return result;
    }

    /// <summary>
    /// Adds a new entry for <paramref name="whenUtc"/>, or updates the existing one for that
    /// timestamp if already present (used by <see cref="MarkSynced"/>, where
    /// <paramref name="weightKg"/> is <see langword="null"/> to mean "keep the existing weight").
    /// </summary>
    private static void Upsert(Context context, DateTimeOffset whenUtc, double? weightKg, bool synced)
    {
        var entries = GetAll(context).ToList();
        int existingIndex = entries.FindIndex(e => e.WhenUtc == whenUtc);
        if (existingIndex >= 0)
            entries[existingIndex] = entries[existingIndex] with { WeightKg = weightKg ?? entries[existingIndex].WeightKg, Synced = synced };
        else
            entries.Add(new Entry(whenUtc, weightKg ?? 0, synced));

        Save(context, entries);
    }

    private static void Save(Context context, List<Entry> entries)
    {
        string raw = string.Join(';', entries.Select(e =>
            $"{e.WhenUtc.UtcTicks}|{e.WeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{(e.Synced ? "1" : "0")}"));

        using var editor = Prefs(context).Edit()!;
        editor.PutString(KeyEntries, raw);
        editor.Apply();
    }
}
