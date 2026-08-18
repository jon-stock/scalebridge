using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ScaleBridge.Permissions;

/// <summary>
/// Runtime permissions required by Prompt.md Section 5: Bluetooth scan/connect on Android 12+,
/// notifications on Android 13+, plus (separately) the Health Connect write permission which is
/// requested through Health Connect's own permission flow, not a plain Android permission
/// dialog - see <see cref="ScaleBridge.Health.HealthConnectWriter"/>.
/// </summary>
public static class PermissionHelper
{
    public static string[] RequiredAndroidPermissions()
    {
        var list = new List<string>();

        if ((int)Build.VERSION.SdkInt >= 31) // Android 12 (S)
        {
            list.Add(Android.Manifest.Permission.BluetoothScan);
            list.Add(Android.Manifest.Permission.BluetoothConnect);
        }

        if ((int)Build.VERSION.SdkInt >= 33) // Android 13 (Tiramisu)
        {
            list.Add(Android.Manifest.Permission.PostNotifications);
        }

        return list.ToArray();
    }

    public static bool HasAllRequiredAndroidPermissions(Context context)
    {
        foreach (var permission in RequiredAndroidPermissions())
        {
            if (context.CheckSelfPermission(permission) != Permission.Granted)
                return false;
        }

        return true;
    }
}
