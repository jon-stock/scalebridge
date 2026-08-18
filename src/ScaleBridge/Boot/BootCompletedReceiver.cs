using Android.App;
using Android.Content;
using Android.Util;
using ScaleBridge.Ble;
using ScaleBridge.Status;

namespace ScaleBridge.Boot;

/// <summary>
/// Re-registers the PendingIntent-backed BLE scan filter after a reboot. Android does not keep
/// system-level BLE scan registrations across a full device restart, so without this the app
/// would silently stop reacting to the scale until manually reopened - Prompt.md Section 5,
/// reliability requirement.
/// </summary>
// Declared explicitly in Properties/AndroidManifest.xml (not via attributes) - see
// ScaleScanReceiver for why this project keeps manifest component wiring manual.
public class BootCompletedReceiver : BroadcastReceiver
{
    private const string LogTag = "ScaleBridge.Boot";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted)
            return;

        if (!ScaleConfig.IsConfigured(context))
        {
            Log.Info(LogTag, "Boot completed but no scale is configured yet; nothing to re-arm.");
            return;
        }

        var result = ScaleScanRegistrar.Register(context);
        Log.Info(LogTag, $"Re-armed scan after boot: {result}");
    }
}
