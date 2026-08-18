using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using ScaleBridge.Status;

namespace ScaleBridge.Ble;

/// <summary>
/// Registers/re-registers the system-level, <see cref="PendingIntent"/>-backed BLE scan filter
/// that lets the OS start (or wake) <see cref="ScaleScanReceiver"/> as soon as the configured
/// scale's advertisement is seen - Prompt.md Section 4, requirement 1. This deliberately does NOT
/// run a continuous foreground scan; the filter is registered once and survives app process death
/// (it is re-armed after reboot by <see cref="Boot.BootCompletedReceiver"/>, since Android does
/// not persist PendingIntent-backed scans across a full restart - Prompt.md Section 5).
/// </summary>
public static class ScaleScanRegistrar
{
    public const string ActionScaleFound = "uk.co.accessuk.scalebridge.ACTION_SCALE_FOUND";
    private const int ScanPendingIntentRequestCode = 100;

    public enum RegisterResult
    {
        Success,
        NotConfigured,
        BluetoothUnavailable,
        MissingPermission,
        ScanFailed,
    }

    public static RegisterResult Register(Context context)
    {
        if (!ScaleConfig.IsConfigured(context))
            return RegisterResult.NotConfigured;

        var bluetoothManager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
        var adapter = bluetoothManager?.Adapter;
        var scanner = adapter?.BluetoothLeScanner;
        if (adapter is null || !adapter.IsEnabled || scanner is null)
            return RegisterResult.BluetoothUnavailable;

        if (!Permissions.PermissionHelper.HasAllRequiredAndroidPermissions(context))
            return RegisterResult.MissingPermission;

        var filters = BuildFilters(context);
        if (filters.Count == 0)
            return RegisterResult.NotConfigured;

        var settings = new ScanSettings.Builder()
            .SetScanMode(ScanMode.LowPower)
            .Build();

        var pendingIntent = BuildPendingIntent(context);

        // Cancel any previous registration first so we don't accumulate duplicate filters across
        // app restarts/reboots.
        try
        {
            scanner.StopScan(pendingIntent);
        }
        catch (Java.Lang.Exception)
        {
            // No previous scan registered - expected on first run.
        }

        int result = (int)scanner.StartScan(filters, settings, pendingIntent);
        return result == 0 ? RegisterResult.Success : RegisterResult.ScanFailed;
    }

    public static void Unregister(Context context)
    {
        var bluetoothManager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
        var scanner = bluetoothManager?.Adapter?.BluetoothLeScanner;
        if (scanner is null)
            return;

        try
        {
            scanner.StopScan(BuildPendingIntent(context));
        }
        catch (Java.Lang.Exception)
        {
            // Ignore - nothing to unregister.
        }
    }

    private static PendingIntent BuildPendingIntent(Context context)
    {
        var intent = new Intent(ActionScaleFound).SetPackage(context.PackageName);
        // FLAG_MUTABLE is required: the system fills in EXTRA_LIST_SCAN_RESULT on this intent
        // before broadcasting it, which is not possible with an immutable PendingIntent.
        var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable;
        return PendingIntent.GetBroadcast(context, ScanPendingIntentRequestCode, intent, flags)!;
    }

    private static IList<ScanFilter> BuildFilters(Context context)
    {
        var filters = new List<ScanFilter>();
        string? address = ScaleConfig.GetDeviceAddress(context);
        string? name = ScaleConfig.GetDeviceName(context);

        if (!string.IsNullOrEmpty(address))
        {
            // A known MAC address is the most reliable filter (Prompt.md open question #3) -
            // one filter is enough since QN-family scales don't rotate their MAC in practice.
            filters.Add(new ScanFilter.Builder().SetDeviceAddress(address).Build()!);
            return filters;
        }

        if (!string.IsNullOrEmpty(name))
        {
            // Fall back to advertised-name matching, combined separately with each known QN
            // vendor service UUID so we don't also need location permission or an overly broad
            // "wake on any BLE device with this name" filter.
            filters.Add(new ScanFilter.Builder().SetDeviceName(name).SetServiceUuid(new Android.OS.ParcelUuid(ScaleGattUuids.ServiceT1)).Build()!);
            filters.Add(new ScanFilter.Builder().SetDeviceName(name).SetServiceUuid(new Android.OS.ParcelUuid(ScaleGattUuids.ServiceT2)).Build()!);
        }

        return filters;
    }
}
