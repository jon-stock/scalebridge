using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Util;
using ScaleBridge.Status;

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

        try
        {
            context.StartForegroundService(serviceIntent);
        }
        catch (Exception ex)
        {
            // Android 12+ (this app targets API 34) restricts which app states are allowed to
            // start a new foreground service from the background - confirmed on a real device as
            // "IllegalStateException: startForegroundService() not allowed due to
            // mAllowStartForeground false" (a Java.Lang.IllegalStateException wrapping a real
            // android.app.ForegroundServiceStartNotAllowedException). This is a genuine OS policy
            // gate, not a bug: a scan-match broadcast delivered via a PendingIntent is not one of
            // Android's exempted broadcast types (unlike e.g. BOOT_COMPLETED), so if the app has
            // no recent foreground/visible state when the scale's advertisement is seen - the
            // exact "no user interaction required" scenario this whole receiver exists for - the
            // OS can refuse to let it start ScaleConnectionService at all, and previously that
            // refusal was an unhandled exception that crashed the whole process.
            //
            // There is no reliable retry here: this is a deterministic app-state gate, not a
            // transient failure, so trying again immediately would just fail the same way. The
            // real, durable fix for "wake a background-restricted app to run a foreground service
            // when a companion BLE device is seen" is Android's CompanionDeviceManager background
            // device-presence observation API plus the
            // android.permission.REQUEST_COMPANION_START_FOREGROUND_SERVICES_FROM_BACKGROUND
            // special permission it grants - see docs/PROTOCOL_CONFIRMATION.md. Until/unless that
            // migration happens, this at least degrades to a clear, actionable notification
            // instead of silently (from the user's perspective) crashing the app.
            Log.Error(LogTag, $"OS refused to start the connection service: {ex}");
            CrashLog.Record(context, ex);
            StatusStore.RecordError(context, $"{ex.GetType().Name}: blocked from starting in the background - open ScaleBridge to sync manually.", DateTimeOffset.UtcNow);
            SyncNotifier.PostError(context, "Scale detected but background sync was blocked by Android this time - open ScaleBridge to sync manually.");
        }
    }
}
