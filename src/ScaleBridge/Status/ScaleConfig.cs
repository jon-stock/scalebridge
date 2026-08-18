using Android.Content;

namespace ScaleBridge.Status;

/// <summary>
/// Persisted one-off setup: which single scale this app should look for. Populated the first
/// time the app runs (see MainActivity) and read back by the scan registrar, the boot receiver,
/// and the connection service. This app is intentionally single-device (Prompt.md Section 1).
/// </summary>
public static class ScaleConfig
{
    private const string PrefsName = "scale_bridge_config";
    private const string KeyDeviceAddress = "device_address";
    private const string KeyDeviceName = "device_name";

    private static ISharedPreferences Prefs(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    public static string? GetDeviceAddress(Context context) =>
        Prefs(context).GetString(KeyDeviceAddress, null);

    public static string? GetDeviceName(Context context) =>
        Prefs(context).GetString(KeyDeviceName, null);

    public static bool IsConfigured(Context context) =>
        !string.IsNullOrEmpty(GetDeviceAddress(context)) || !string.IsNullOrEmpty(GetDeviceName(context));

    public static void Save(Context context, string? deviceAddress, string? deviceName)
    {
        using var editor = Prefs(context).Edit()!;
        editor.PutString(KeyDeviceAddress, deviceAddress);
        editor.PutString(KeyDeviceName, deviceName);
        editor.Apply();
    }
}
