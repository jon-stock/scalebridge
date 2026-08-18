using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Util;

namespace ScaleBridge.Ble;

/// <summary>
/// Receives the broadcast the OS sends via the PendingIntent registered in
/// <see cref="ScaleScanRegistrar"/> once the configured scale's advertisement matches - this is
/// what lets the app react "as soon as it powers on" without a manual connect button
/// (Prompt.md Section 4, requirement 1), including after the app process has been killed.
/// </summary>
// Declared explicitly in Properties/AndroidManifest.xml (not via attributes) so the exported
// receiver + intent-filter wiring for the scan-wake broadcast is easy to audit in one place.
//
// [Register] is required, not optional, for that hand-written manifest entry to actually work:
// .NET for Android does not give managed classes a Java name that literally matches their C#
// namespace by default (it generates a hashed name instead, e.g. MainActivity's real Java name
// is "crc6427e3e38310646c4d.MainActivity" - only correct in the manifest because [Activity]
// generates that entry for us). Properties/AndroidManifest.xml's ".Ble.ScaleScanReceiver" entry
// resolves to the literal Java class "uk.co.accessuk.scalebridge.Ble.ScaleScanReceiver", which
// did not exist without this attribute forcing it - causing
// "Unable to instantiate receiver ...: ClassNotFoundException" the first time this receiver was
// ever actually triggered by the OS (see docs/PROTOCOL_CONFIRMATION.md).
[Android.Runtime.Register("uk.co.accessuk.scalebridge.Ble.ScaleScanReceiver")]
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

        // Bound as the non-generic, Parcelable-returning overload (returns a raw, untyped
        // IList); cast each entry back to ScanResult ourselves rather than relying on a
        // generic/typed overload that may not be present on every binding version.
        var results = intent.GetParcelableArrayListExtra(BluetoothLeScanner.ExtraListScanResult);
        BluetoothDevice? device = null;
        if (results is not null)
        {
            foreach (var raw in results)
            {
                if (raw is ScanResult { Device: { } scanResultDevice })
                {
                    device = scanResultDevice;
                    break;
                }
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
