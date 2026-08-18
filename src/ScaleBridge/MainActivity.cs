using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Android.Widget;
using ScaleBridge.Ble;
using ScaleBridge.Health;
using ScaleBridge.Permissions;
using ScaleBridge.Status;

namespace ScaleBridge;

/// <summary>
/// The "very basic screen ... no need for a polished UI" required by Prompt.md Section 4,
/// requirement 7, plus the one-off setup flow: grant permissions, identify the specific scale by
/// MAC address (via a short debug BLE scan), and register the wake-scan filter. Once configured,
/// the app runs unattended via <see cref="Ble.ScaleScanReceiver"/> and
/// <see cref="Ble.ScaleConnectionService"/> - this Activity is not needed again unless the user
/// wants to check status or re-run setup.
/// </summary>
[Activity(Label = "ScaleBridge", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private const int RequestCodeAndroidPermissions = 1001;
    private const int RequestCodeHealthConnectPermission = 1002;
    private const long DebugScanDurationMs = 15_000;

    private TextView _tvStatus = null!;
    private TextView _tvScanLog = null!;
    private TextView _tvLastSync = null!;
    private EditText _etMac = null!;
    private EditText _etName = null!;

    private ScanCallback? _debugScanCallback;
    private readonly Dictionary<string, string> _seenDevices = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _tvStatus = FindViewById<TextView>(Resource.Id.tvStatus)!;
        _tvScanLog = FindViewById<TextView>(Resource.Id.tvScanLog)!;
        _tvLastSync = FindViewById<TextView>(Resource.Id.tvLastSync)!;
        _etMac = FindViewById<EditText>(Resource.Id.etMac)!;
        _etName = FindViewById<EditText>(Resource.Id.etName)!;

        FindViewById<Button>(Resource.Id.btnPermissions)!.Click += (_, _) => RequestAndroidPermissions();
        FindViewById<Button>(Resource.Id.btnHealthConnect)!.Click += (_, _) => RequestHealthConnectPermission();
        FindViewById<Button>(Resource.Id.btnScanDebug)!.Click += (_, _) => RunDebugScan();
        FindViewById<Button>(Resource.Id.btnSave)!.Click += (_, _) => SaveConfigurationAndArmScan();

        var existingAddress = ScaleConfig.GetDeviceAddress(this);
        var existingName = ScaleConfig.GetDeviceName(this);
        if (!string.IsNullOrEmpty(existingAddress))
            _etMac.Text = existingAddress;
        if (!string.IsNullOrEmpty(existingName))
            _etName.Text = existingName;
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool configured = ScaleConfig.IsConfigured(this);
        bool hasPermissions = PermissionHelper.HasAllRequiredAndroidPermissions(this);
        bool healthConnectAvailable = HealthConnectWriter.IsAvailable(this);

        _tvStatus.Text =
            $"Configured: {(configured ? "yes" : "no")}\n" +
            $"Bluetooth/notification permissions granted: {(hasPermissions ? "yes" : "no")}\n" +
            $"Health Connect available: {(healthConnectAvailable ? "yes" : "no")}";

        var lastSync = StatusStore.LastSyncUtc(this);
        var lastWeight = StatusStore.LastWeightKg(this);
        var lastError = StatusStore.LastError(this);

        if (lastError is not null)
        {
            _tvLastSync.Text = $"Last error ({StatusStore.LastErrorUtc(this)?.ToLocalTime():g}): {lastError}";
        }
        else if (lastSync is not null && lastWeight is not null)
        {
            _tvLastSync.Text = $"{lastWeight:0.0} kg at {lastSync.Value.ToLocalTime():g}";
        }
        else
        {
            _tvLastSync.Text = "No sync yet.";
        }
    }

    // ---- Step 1: permissions ----------------------------------------------------------------

    private void RequestAndroidPermissions()
    {
        var missing = PermissionHelper.RequiredAndroidPermissions();
        if (missing.Length == 0)
        {
            Toast.MakeText(this, "No extra permissions needed on this Android version.", ToastLength.Short)!.Show();
            return;
        }

        RequestPermissions(missing, RequestCodeAndroidPermissions);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == RequestCodeAndroidPermissions)
            RefreshStatus();
    }

    private void RequestHealthConnectPermission()
    {
        if (!HealthConnectWriter.IsAvailable(this))
        {
            Toast.MakeText(this, "Health Connect is not installed/available on this device yet.", ToastLength.Long)!.Show();
            return;
        }

        // Health Connect's WRITE_WEIGHT permission is a normal Android runtime permission on
        // Android 14+ (where Health Connect is built into the platform) and is requestable the
        // same way as any other dangerous permission. On Android 9-13 (separate Health Connect
        // app), some connect-client versions instead require the
        // PermissionController.createRequestPermissionResultContract() ActivityResultContract
        // flow - if this simple request silently does nothing on such a device, see
        // docs/SETUP.md for the alternative flow to wire up.
        RequestPermissions(new[] { "android.permission.health.WRITE_WEIGHT" }, RequestCodeHealthConnectPermission);
    }

    // ---- Step 2: identify the scale ---------------------------------------------------------

    private void RunDebugScan()
    {
        if (!PermissionHelper.HasAllRequiredAndroidPermissions(this))
        {
            Toast.MakeText(this, "Grant Bluetooth permissions first.", ToastLength.Long)!.Show();
            return;
        }

        var bluetoothManager = (BluetoothManager?)GetSystemService(BluetoothService);
        var scanner = bluetoothManager?.Adapter?.BluetoothLeScanner;
        if (scanner is null)
        {
            Toast.MakeText(this, "Bluetooth is off or unavailable.", ToastLength.Long)!.Show();
            return;
        }

        _seenDevices.Clear();
        _tvScanLog.Text = "Scanning...";
        _debugScanCallback = new DebugScanCallback(this);
        scanner.StartScan(_debugScanCallback);

        new Handler(Looper.MainLooper!).PostDelayed(() =>
        {
            try
            {
                scanner.StopScan(_debugScanCallback);
            }
            catch (Java.Lang.Exception)
            {
                // Bluetooth may have been toggled off mid-scan; nothing to clean up.
            }

            _tvScanLog.Text = _seenDevices.Count == 0
                ? "No BLE devices seen. Make sure the scale is powered on and try again."
                : string.Join("\n", _seenDevices.Values);
        }, DebugScanDurationMs);
    }

    private void OnDeviceSeen(BluetoothDevice device, int rssi)
    {
        string name = device.Name ?? "(unnamed)";
        _seenDevices[device.Address] = $"{device.Address}  {name}  rssi={rssi}";
        RunOnUiThread(() => _tvScanLog.Text = string.Join("\n", _seenDevices.Values));
    }

    private sealed class DebugScanCallback : ScanCallback
    {
        private readonly MainActivity _owner;
        public DebugScanCallback(MainActivity owner) => _owner = owner;

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is not null)
                _owner.OnDeviceSeen(result.Device, result.Rssi);
        }
    }

    // ---- Step 3: save + arm the wake-scan -----------------------------------------------------

    private void SaveConfigurationAndArmScan()
    {
        string? mac = _etMac.Text?.Trim();
        string? name = _etName.Text?.Trim();

        if (string.IsNullOrEmpty(mac) && string.IsNullOrEmpty(name))
        {
            Toast.MakeText(this, "Enter a MAC address (preferred) or an advertised name.", ToastLength.Long)!.Show();
            return;
        }

        ScaleConfig.Save(this, string.IsNullOrEmpty(mac) ? null : mac, string.IsNullOrEmpty(name) ? null : name);

        var result = ScaleScanRegistrar.Register(this);
        Toast.MakeText(this, $"Scan registration: {result}", ToastLength.Long)!.Show();
        RefreshStatus();
    }
}
