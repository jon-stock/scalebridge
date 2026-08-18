using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Android.Util;
using ScaleBridge.Health;
using ScaleBridge.Status;

namespace ScaleBridge.Ble;

/// <summary>
/// Foreground service that owns the short-lived connect -> handshake -> capture -> write ->
/// disconnect sequence (Prompt.md Section 4, requirement 6: "no user interaction required after
/// setup"). Started by <see cref="ScaleScanReceiver"/> once the scale's advertisement is seen.
/// A foreground service (rather than a plain background service) is required because Android
/// restricts what an app woken up via a broadcast can do in the background on modern Android
/// versions, and because the GATT connection window needs to reliably outlive the triggering
/// broadcast.
/// </summary>
// Declared explicitly in Properties/AndroidManifest.xml (not via attributes) - see
// ScaleScanReceiver for why this project keeps manifest component wiring manual.
public class ScaleConnectionService : Service
{
    public const string ExtraDeviceAddress = "device_address";
    private const string LogTag = "ScaleBridge.Service";
    private const long OverallTimeoutMs = 45_000;

    private QnScaleSession? _session;
    private Handler? _timeoutHandler;
    private bool _finished;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string? address = intent?.GetStringExtra(ExtraDeviceAddress);
        StartForeground(SyncNotifier.ForegroundNotificationId, SyncNotifier.BuildForegroundNotification(this, "Connecting to scale..."));

        if (string.IsNullOrEmpty(address))
        {
            Log.Warn(LogTag, "Started with no device address; stopping.");
            StopSelfSafely();
            return StartCommandResult.NotSticky;
        }

        var bluetoothManager = (BluetoothManager?)GetSystemService(BluetoothService);
        var adapter = bluetoothManager?.Adapter;
        var device = adapter?.GetRemoteDevice(address);
        if (adapter is null || device is null)
        {
            FailAndStop("Bluetooth adapter unavailable.");
            return StartCommandResult.NotSticky;
        }

        _finished = false;
        _session = new QnScaleSession();
        _session.StatusChanged += OnStatusChanged;
        _session.WeightCaptured += OnWeightCaptured;
        _session.Failed += OnFailed;
        _session.Disconnected += OnDisconnected;
        _session.Connect(this, device);

        _timeoutHandler = new Handler(Looper.MainLooper!);
        _timeoutHandler.PostDelayed(() =>
        {
            if (!_finished)
                FailAndStop("Timed out waiting for a stable weight reading (scale may not have been stepped on).", isError: false);
        }, OverallTimeoutMs);

        return StartCommandResult.NotSticky;
    }

    private void OnStatusChanged(string status)
    {
        Log.Info(LogTag, status);
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(SyncNotifier.ForegroundNotificationId, SyncNotifier.BuildForegroundNotification(this, status));
    }

    private void OnWeightCaptured(double weightKg)
    {
        if (_finished)
            return;

        _finished = true;
        var whenUtc = DateTimeOffset.UtcNow;

        // Run the Health Connect write off the main thread; HealthConnectWriter blocks its
        // calling thread while bridging the underlying Kotlin coroutine call (see
        // docs/PROTOCOL_CONFIRMATION.md and Health/HealthConnectWriter.cs for why).
        Task.Run(async () =>
        {
            try
            {
                await HealthConnectWriter.WriteWeightAsync(this, weightKg, whenUtc);
                StatusStore.RecordSuccess(this, weightKg, whenUtc);
                SyncNotifier.PostSuccess(this, weightKg, whenUtc.ToLocalTime());
                Log.Info(LogTag, $"Wrote {weightKg:0.0} kg to Health Connect.");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to write to Health Connect: {ex}");
                StatusStore.RecordError(this, ex.Message, DateTimeOffset.UtcNow);
                SyncNotifier.PostError(this, $"Captured {weightKg:0.0} kg but Health Connect write failed: {ex.Message}");
            }
            finally
            {
                Disconnect();
                StopSelfSafely();
            }
        });
    }

    private void OnFailed(string message) => FailAndStop(message, isError: true);

    private void OnDisconnected()
    {
        if (_finished)
            return;

        // The scale disconnected before we ever saw a stable weight - not necessarily an error
        // (e.g. it powered on briefly with nobody standing on it), so this is logged as
        // informational rather than surfaced as an error notification.
        _finished = true;
        Log.Info(LogTag, "Scale disconnected before a stable weight was captured.");
        StopSelfSafely();
    }

    private void FailAndStop(string message, bool isError)
    {
        if (_finished)
            return;

        _finished = true;
        Log.Warn(LogTag, message);
        if (isError)
        {
            StatusStore.RecordError(this, message, DateTimeOffset.UtcNow);
            SyncNotifier.PostError(this, message);
        }

        Disconnect();
        StopSelfSafely();
    }

    private void Disconnect()
    {
        _session?.Close();
        _session = null;
    }

    private void StopSelfSafely()
    {
        _timeoutHandler?.RemoveCallbacksAndMessages(null);
        // Deprecated overload used deliberately for uniform behaviour across API 26-34 (see
        // similar notes on the deprecated GATT read/write APIs in QnScaleSession.cs).
#pragma warning disable CS0618
        StopForeground(true);
#pragma warning restore CS0618
        StopSelf();
    }
}
