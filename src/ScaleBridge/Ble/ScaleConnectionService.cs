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
// ScaleScanReceiver for why this project keeps manifest component wiring manual, and for why
// [Register] below is required (the same Java-class-name mismatch that crashed
// ScaleScanReceiver applies here too - this service just hadn't been started yet, since that
// only happens once ScaleScanReceiver itself successfully runs).
[Android.Runtime.Register("uk.co.accessuk.scalebridge.Ble.ScaleConnectionService")]
public class ScaleConnectionService : Service
{
    public const string ExtraDeviceAddress = "device_address";
    private const string LogTag = "ScaleBridge.Service";

    // Padded beyond a single connect attempt's worth of time to leave room for
    // QnScaleSession's own connect-phase retries (see MaxConnectAttempts/ConnectRetryDelaysMs in
    // QnScaleSession.cs) - a status-133-style failure that needs 1-2 retries should still have
    // the full handshake/weight-capture window available afterwards, not be starved by a timeout
    // budget sized only for a single successful attempt.
    private const long OverallTimeoutMs = 60_000;

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
        BluetoothDevice? device = null;
        try
        {
            device = adapter?.GetRemoteDevice(address);
        }
        catch (Java.Lang.Exception ex)
        {
            // GetRemoteDevice throws IllegalArgumentException for a malformed MAC address rather
            // than returning null - previously uncaught here, which would crash the whole service
            // (and, since this runs in the app's main process, the whole app) with an unhandled
            // exception instead of the ordinary "can't sync this time" failure this really is.
            // ScaleConfig only ever stores an address the user entered/picked from a real scan
            // result, so this should not normally happen - but that also describes exactly the
            // kind of "different unhandled exception after it having worked fine for days"
            // reports this project has repeatedly seen turn out to have a genuine, previously
            // uncaught cause (see docs/PROTOCOL_CONFIRMATION.md).
            CrashLog.Record(this, ex);
            FailAndStop($"Invalid scale Bluetooth address ({ex.GetType().Name}): {ex.Message}", isError: true);
            return StartCommandResult.NotSticky;
        }

        if (adapter is null || device is null)
        {
            FailAndStop("Bluetooth adapter unavailable.", isError: true);
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
            if (_finished)
                return;

            // Previously this always gave up completely silently (isError: false, no
            // notification, no StatusStore/History trace at all) on the assumption that a
            // timeout only ever means "the scale just wasn't stepped on" - but a real stalled
            // handshake (e.g. stuck partway through the 0x14/0x21 acknowledgement dance) looks
            // identical from here, and previously left literally zero evidence anywhere that a
            // sync had even been attempted. HasReceivedAnyVendorData distinguishes the two: no
            // vendor traffic at all really does just mean nobody stood on the scale (kept
            // silent, as before); any vendor traffic followed by a timeout means the handshake
            // itself got stuck, which is now surfaced as a real, diagnosable error.
            bool handshakeStalled = _session?.HasReceivedAnyVendorData == true;
            FailAndStop(
                handshakeStalled
                    ? "Connected to the scale and started the handshake, but timed out before a stable weight was produced."
                    : "Timed out waiting for a stable weight reading (scale may not have been stepped on).",
                isError: handshakeStalled);
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
                WeightHistoryStore.RecordSynced(this, weightKg, whenUtc);
                SyncNotifier.PostSuccess(this, weightKg, whenUtc.ToLocalTime());
                Log.Info(LogTag, $"Wrote {weightKg:0.0} kg to Health Connect.");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to write to Health Connect: {ex}");

                // ex.Message alone is frequently useless for Java/JNI exceptions surfaced through
                // this bridge (e.g. a bare ClassNotFoundException's message is just the class
                // name, with no indication of what threw or why) - see
                // docs/PROTOCOL_CONFIRMATION.md. CrashLog.Record persists the full ex.ToString()
                // (managed exception chain, plus the underlying Java stack trace/"Caused by"
                // section for JNI exceptions) both to SharedPreferences, surfaced by MainActivity's
                // existing "Last crash" card, and to crash_log.txt for adb/MTP retrieval - without
                // this actually being a fatal, unhandled crash.
                CrashLog.Record(this, ex);

                // Don't just lose this reading because one sync attempt failed - persist it so
                // MainActivity can show it (in the History list, marked pending) and let the user
                // retry the write later without needing to step back on the scale to capture the
                // same weight again.
                WeightHistoryStore.RecordPending(this, weightKg, whenUtc);

                // A revoked WRITE_WEIGHT grant surfaces here as a bare Java SecurityException
                // with no other indication of what went wrong - the same "confusing raw exception
                // text" pattern seen with ClassNotFoundException earlier (see
                // docs/PROTOCOL_CONFIRMATION.md). Calling it out by name specifically is worth
                // doing here since it's an actionable, user-fixable state (re-grant the
                // permission), unlike most other failures in this catch block.
                bool permissionRevoked = ex is Java.Lang.SecurityException
                    || (ex.InnerException is Java.Lang.SecurityException);

                string shortDetail = permissionRevoked
                    ? "Health Connect write permission was revoked"
                    : $"{ex.GetType().Name}: {ex.Message}";
                StatusStore.RecordError(this, $"{shortDetail} (see \"Last crash\" for full details)", DateTimeOffset.UtcNow);
                SyncNotifier.PostError(this,
                    permissionRevoked
                        ? $"Captured {weightKg:0.0} kg but Health Connect write permission was revoked. Open ScaleBridge and re-grant it, then retry from the app."
                        : $"Captured {weightKg:0.0} kg but Health Connect write failed: {shortDetail}. Saved - retry from the app.");
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

    /// <summary>
    /// Safety net for every other teardown path in this class not having run first (e.g. the OS
    /// killing this service directly - low memory, battery-manager force-stop, or any other
    /// termination that does not go through <see cref="StopSelfSafely"/> first). Without this,
    /// an abruptly-killed service could leave its <see cref="QnScaleSession"/>'s
    /// <c>BluetoothGatt</c> connection registered with the Android Bluetooth stack indefinitely -
    /// a real, cumulative resource leak (Android enforces a low per-app limit on concurrent GATT
    /// client registrations) that would not show up as any particular bug in this app's own code,
    /// but plausibly explains a background-scan device that "worked fine for days, then started
    /// failing to connect at all" until Bluetooth was toggled or the phone rebooted. Calling
    /// <see cref="Disconnect"/>/removing handler callbacks here is idempotent with every other
    /// call site, so this is safe to run unconditionally.
    /// </summary>
    public override void OnDestroy()
    {
        _timeoutHandler?.RemoveCallbacksAndMessages(null);
        Disconnect();
        base.OnDestroy();
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
