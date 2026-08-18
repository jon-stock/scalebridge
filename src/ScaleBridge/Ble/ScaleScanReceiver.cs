using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Util;
using System.Linq;

namespace ScaleBridge.Ble;

/// <summary>
/// Receives the broadcast the OS sends via the PendingIntent registered in
/// <see cref="ScaleScanRegistrar"/> once the configured scale's advertisement matches - this is
/// what lets the app react "as soon as it powers on" without a manual connect button
/// (Prompt.md Section 4, requirement 1), including after the app process has been killed.
/// </summary>
// Declared explicitly in Properties/AndroidManifest.xml (not via attributes) so the exported
// receiver + intent-filter wiring for the scan-wake broadcast is easy to audit in one place.
public class ScaleScanReceiver : BroadcastReceiver
{
    private const string LogTag = "ScaleBridge.Scan";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null || intent.Action != ScaleScanRegistrar.ActionScaleFound)
            return;

        int errorCode = intent.GetIntExtra(BluetoothLeScanner.ExtraErrorCode, 0);
        if (errorCode != 0)
        {
            Log.Warn(LogTag, $"BLE scan callback reported error code {errorCode}");
            return;
        }

        // Bound as the non-generic, Parcelable-returning overload; cast each entry back to
        // ScanResult ourselves rather than relying on a generic/typed overload that may not be
        // present on every binding version.
        var results = intent.GetParcelableArrayListExtra(BluetoothLeScanner.ExtraListScanResult);
        BluetoothDevice? device = null;
        foreach (var raw in results ?? Enumerable.Empty<Android.OS.IParcelable>())
        {
            if (raw is ScanResult { Device: { } scanResultDevice })
            {
                device = scanResultDevice;
                break;
            }
        }

        if (device is null)
        {
            Log.Warn(LogTag, "Scan-matched broadcast contained no usable scan result.");
            return;
        }

        Log.Info(LogTag, $"Scale advertisement matched: {device.Address}. Starting connection service.");

        var serviceIntent = new Intent(context, typeof(ScaleConnectionService))
            .PutExtra(ScaleConnectionService.ExtraDeviceAddress, device.Address);

        context.StartForegroundService(serviceIntent);
    }
}
