using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.Activity.Result;
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
///
/// Extends AndroidX's <see cref="ComponentActivity"/> (rather than the plain framework
/// <c>Activity</c>) specifically to get <c>RegisterForActivityResult</c>, needed for the Health
/// Connect permission flow below.
///
/// The two extra intent-filters below are a documented Health Connect requirement (confirmed
/// against Google's own Health Connect sample app manifest), not something optional: without an
/// activity somewhere in the app declaring these, Health Connect refuses to let the app request
/// permissions directly at all, and instead shows a "manage this from Health Connect's own
/// settings" redirect message - exactly what was seen before this was added. This activity
/// doesn't need any special handling code for them: for this small, single-user app, simply
/// opening the normal main screen (already what happens by default) is a reasonable "rationale"
/// destination.
/// </summary>
[Activity(Label = "ScaleBridge", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
[IntentFilter(new[] { "androidx.health.ACTION_SHOW_PERMISSIONS_RATIONALE" })]
[IntentFilter(new[] { "android.intent.action.VIEW_PERMISSION_USAGE" }, Categories = new[] { "android.intent.category.HEALTH_PERMISSIONS" })]
public class MainActivity : ComponentActivity
{
    private const int RequestCodeAndroidPermissions = 1001;
    private const long DebugScanDurationMs = 15_000;
    private const long AutoRefreshIntervalMs = 3_000;

    private TextView _tvStatus = null!;
    private TextView _tvScanEmpty = null!;
    private TextView _tvHealthConnectPermissionStatus = null!;
    private ListView _lvDevices = null!;
    private EditText _etMac = null!;
    private EditText _etName = null!;

    // Setup (status + steps 1-3) is visible by default until the scale is configured, then
    // hidden - now that the app is past its initial MVP stage, there's no need to keep showing
    // one-off setup steps every time it's opened. Toggled back on via the overflow menu (e.g. to
    // reconfigure a different scale later); "Last crash"/"Last error" moved the same way, into a
    // menu-triggered dialog (see ShowDiagnosticsDialog) instead of an always-visible card.
    private View _setupSection = null!;
    private bool _setupSectionVisible;

    private TextView _tvHistoryRange = null!;
    private TextView _tvHistoryEmpty = null!;
    private ListView _lvHistory = null!;
    private Button _btnHistoryOlder = null!;
    private Button _btnHistoryNewer = null!;
    private Button _btnRetryPending = null!;
    private ArrayAdapter<string>? _historyListAdapter;

    // 0 = the most recent 7 days (today back to 6 days ago); 1 = the 7 days before that; etc. -
    // Prompt.md's history list is "paginated after a week", i.e. one rolling 7-day page at a time
    // rather than a single unbounded scroll of the app's entire history.
    private int _historyWeekOffset;

    private ScanCallback? _debugScanCallback;
    private readonly Dictionary<string, string> _seenDevices = new();
    private readonly List<string> _deviceAddressesInOrder = new();
    private ArrayAdapter<string>? _deviceListAdapter;

    private ActivityResultLauncher? _healthPermissionLauncher;
    private string? _lastHealthConnectPermissionResult;

    // Polls RefreshStatus() while the screen is visible, so the History list (including any
    // newly-pending entry) reflects background activity (a scale-triggered sync running via
    // ScaleScanReceiver + ScaleConnectionService) without the user needing to close and reopen
    // the app to see it - there was previously no way to observe that short of doing exactly
    // that. Started in OnResume and stopped in OnPause, matching the existing manual
    // RefreshStatus() call sites.
    private readonly Handler _autoRefreshHandler = new(Looper.MainLooper!);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Must be registered before the activity reaches STARTED - i.e. here in OnCreate, not
        // lazily when the button is tapped. Wrapped defensively: this calls into genuinely new,
        // never-run-on-a-device binding surface (see HealthConnectWriter.CreatePermissionRequestContract),
        // and a crash here previously took the whole app down on every launch before this screen
        // even rendered. If it fails, the button below degrades gracefully instead, and the
        // failure is captured for the Diagnostics menu via CrashLog.
        try
        {
            _healthPermissionLauncher = RegisterForActivityResult(
                HealthConnectWriter.CreatePermissionRequestContract(),
                new HealthPermissionResultCallback(this));
        }
        catch (Exception ex)
        {
            CrashLog.Record(this, ex);
            _healthPermissionLauncher = null;
        }

        SetContentView(Resource.Layout.activity_main);

        _tvStatus = FindViewById<TextView>(Resource.Id.tvStatus)!;
        _tvScanEmpty = FindViewById<TextView>(Resource.Id.tvScanEmpty)!;
        _tvHealthConnectPermissionStatus = FindViewById<TextView>(Resource.Id.tvHealthConnectPermissionStatus)!;
        _lvDevices = FindViewById<ListView>(Resource.Id.lvDevices)!;
        _etMac = FindViewById<EditText>(Resource.Id.etMac)!;
        _etName = FindViewById<EditText>(Resource.Id.etName)!;

        _setupSection = FindViewById<View>(Resource.Id.setupSection)!;
        _setupSectionVisible = !ScaleConfig.IsConfigured(this);

        _tvHistoryRange = FindViewById<TextView>(Resource.Id.tvHistoryRange)!;
        _tvHistoryEmpty = FindViewById<TextView>(Resource.Id.tvHistoryEmpty)!;
        _lvHistory = FindViewById<ListView>(Resource.Id.lvHistory)!;
        _btnHistoryOlder = FindViewById<Button>(Resource.Id.btnHistoryOlder)!;
        _btnHistoryNewer = FindViewById<Button>(Resource.Id.btnHistoryNewer)!;
        _btnRetryPending = FindViewById<Button>(Resource.Id.btnRetryPending)!;
        _btnHistoryOlder.Click += (_, _) => { _historyWeekOffset++; RefreshHistory(); };
        _btnHistoryNewer.Click += (_, _) =>
        {
            if (_historyWeekOffset > 0)
            {
                _historyWeekOffset--;
                RefreshHistory();
            }
        };
        _btnRetryPending.Click += (_, _) => RetryPendingSyncsAsync();

        _historyListAdapter = new ArrayAdapter<string>(this, Resource.Layout.list_item_history, Resource.Id.tvHistoryRow, new List<string>());
        _lvHistory.Adapter = _historyListAdapter;

        _deviceListAdapter = new ArrayAdapter<string>(this, Resource.Layout.list_item_device, Resource.Id.tvDeviceRow, new List<string>());
        _lvDevices.Adapter = _deviceListAdapter;
        _lvDevices.ItemClick += OnDeviceRowClicked;

        // A plain ListView nested inside an outer ScrollView is a well-known Android trap: the
        // ScrollView intercepts touch/drag gestures before the list ever sees them, so the list
        // itself can't be scrolled once it has more items than fit in its fixed height. Telling
        // the parent not to intercept touches that start on the list (while still letting the
        // list handle them normally - e.Handled is left false) is the standard fix. Applies to
        // both nested lists.
        _lvDevices.Touch += (_, e) =>
        {
            _lvDevices.Parent?.RequestDisallowInterceptTouchEvent(true);
            e.Handled = false;
        };
        _lvHistory.Touch += (_, e) =>
        {
            _lvHistory.Parent?.RequestDisallowInterceptTouchEvent(true);
            e.Handled = false;
        };

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

    public override bool OnCreateOptionsMenu(IMenu? menu)
    {
        MenuInflater!.Inflate(Resource.Menu.main_menu, menu);
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        if (item.ItemId == Resource.Id.menuToggleSetup)
        {
            _setupSectionVisible = !_setupSectionVisible;
            RefreshStatus();
            return true;
        }

        if (item.ItemId == Resource.Id.menuDiagnostics)
        {
            ShowDiagnosticsDialog();
            return true;
        }

        return base.OnOptionsItemSelected(item);
    }

    /// <summary>
    /// "Last crash"/"Last error" used to be always-visible cards on the main screen - moved here,
    /// behind the overflow menu, now that the app is past its initial MVP stage and these are
    /// only relevant when something has actually gone wrong.
    /// </summary>
    private void ShowDiagnosticsDialog()
    {
        var lastCrashUtc = CrashLog.LastCrashUtc(this);
        var lastCrashText = CrashLog.LastCrashText(this);
        var lastError = StatusStore.LastError(this);

        string message = lastCrashText is not null
            ? $"Last crash ({lastCrashUtc?.ToLocalTime():g}):\n\n{lastCrashText}"
            : lastError is not null
                ? $"Last error ({StatusStore.LastErrorUtc(this)?.ToLocalTime():g}):\n\n{lastError}"
                : "No crashes or errors recorded.";

        var builder = new AlertDialog.Builder(this)!
            .SetTitle("Diagnostics")!
            .SetMessage(message)!
            .SetPositiveButton("OK", (EventHandler<DialogClickEventArgs>?)null)!;

        if (lastCrashText is not null)
        {
            builder.SetNegativeButton("Clear", (_, _) =>
            {
                CrashLog.Clear(this);
                RefreshStatus();
            });
        }

        builder.Show();
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshStatus();
        ScheduleAutoRefresh();
    }

    protected override void OnPause()
    {
        base.OnPause();
        // Stop polling once the screen isn't visible - there's nothing useful to refresh in the
        // UI while it isn't shown, and this avoids running indefinitely in the background.
        _autoRefreshHandler.RemoveCallbacksAndMessages(null);
    }

    private void ScheduleAutoRefresh()
    {
        _autoRefreshHandler.PostDelayed(() =>
        {
            RefreshStatus();
            ScheduleAutoRefresh();
        }, AutoRefreshIntervalMs);
    }

    private void RefreshStatus()
    {
        _setupSection.Visibility = _setupSectionVisible ? ViewStates.Visible : ViewStates.Gone;

        bool configured = ScaleConfig.IsConfigured(this);
        bool hasPermissions = PermissionHelper.HasAllRequiredAndroidPermissions(this);
        bool healthConnectAvailable = HealthConnectWriter.IsAvailable(this);

        _tvStatus.Text =
            $"Configured: {(configured ? "yes" : "no")}\n" +
            $"Bluetooth/notification permissions granted: {(hasPermissions ? "yes" : "no")}\n" +
            $"Health Connect available: {(healthConnectAvailable ? "yes" : "no")}";

        _tvHealthConnectPermissionStatus.Text = _lastHealthConnectPermissionResult ?? "Not requested yet in this session.";

        RefreshHistory();
    }

    /// <summary>
    /// Renders the current one-week page of <see cref="WeightHistoryStore"/>, newest-first within
    /// the page. Week 0 is the most recent 7 days (today back to 6 days ago, in local time -
    /// simple rolling windows rather than calendar weeks, to sidestep any Monday-vs-Sunday
    /// start-of-week question); week N is the 7 days before week N-1. "Retry pending now" reflects
    /// pending readings across *all* history, not just the visible page, since a failed reading
    /// from a week ago is just as retryable as one from today.
    /// </summary>
    private void RefreshHistory()
    {
        var all = WeightHistoryStore.GetAll(this); // newest first

        var nowLocal = DateTimeOffset.Now;
        var todayLocalMidnight = new DateTimeOffset(nowLocal.Date, nowLocal.Offset);
        var pageEndExclusiveLocal = todayLocalMidnight.AddDays(1 - 7 * _historyWeekOffset);
        var pageStartLocal = pageEndExclusiveLocal.AddDays(-7);
        var pageStartUtc = pageStartLocal.ToUniversalTime();
        var pageEndExclusiveUtc = pageEndExclusiveLocal.ToUniversalTime();

        var pageEntries = all.Where(e => e.WhenUtc >= pageStartUtc && e.WhenUtc < pageEndExclusiveUtc).ToList();

        _historyListAdapter?.Clear();
        if (pageEntries.Count == 0)
        {
            _tvHistoryEmpty.Visibility = ViewStates.Visible;
            _lvHistory.Visibility = ViewStates.Gone;
        }
        else
        {
            _tvHistoryEmpty.Visibility = ViewStates.Gone;
            _lvHistory.Visibility = ViewStates.Visible;
            foreach (var entry in pageEntries)
            {
                var local = entry.WhenUtc.ToLocalTime();
                string line = $"{local:dd MMM yyyy HH:mm}   {entry.WeightKg:0.0} kg";
                if (!entry.Synced)
                    line += "   (pending retry)";
                _historyListAdapter?.Add(line);
            }
        }

        var rangeStartLocal = pageStartLocal;
        var rangeEndLocal = pageEndExclusiveLocal.AddDays(-1);
        _tvHistoryRange.Text = _historyWeekOffset == 0
            ? $"This week ({rangeStartLocal:dd MMM} - {rangeEndLocal:dd MMM})"
            : $"{rangeStartLocal:dd MMM yyyy} - {rangeEndLocal:dd MMM yyyy}";

        _btnHistoryNewer.Enabled = _historyWeekOffset > 0;

        var pending = WeightHistoryStore.GetPending(this);
        _btnRetryPending.Visibility = pending.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>
    /// Retries every reading in <see cref="WeightHistoryStore.GetPending"/> (captured from the
    /// scale but not yet successfully written to Health Connect - see
    /// <see cref="Ble.ScaleConnectionService"/>) directly from the UI, on the caller's (main)
    /// thread inside an <see langword="async"/> method - unlike
    /// <see cref="Ble.ScaleConnectionService"/>, which deliberately runs the same call via
    /// <see cref="Task.Run"/> because it has no UI thread of its own to safely block. A reading is
    /// only marked synced once it has actually, successfully been written - anything that fails
    /// again is left pending for the next retry.
    /// </summary>
    private async void RetryPendingSyncsAsync()
    {
        var pending = WeightHistoryStore.GetPending(this);
        if (pending.Count == 0)
            return;

        _btnRetryPending.Enabled = false;
        int succeeded = 0;
        int stillFailing = 0;
        try
        {
            foreach (var entry in pending)
            {
                try
                {
                    await HealthConnectWriter.WriteWeightAsync(this, entry.WeightKg, entry.WhenUtc);
                    WeightHistoryStore.MarkSynced(this, entry.WhenUtc);
                    StatusStore.RecordSuccess(this, entry.WeightKg, entry.WhenUtc);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    CrashLog.Record(this, ex);
                    stillFailing++;
                }
            }
        }
        finally
        {
            _btnRetryPending.Enabled = true;
        }

        Toast.MakeText(this,
            stillFailing == 0
                ? $"Synced {succeeded} pending reading(s)."
                : $"Synced {succeeded} pending reading(s); {stillFailing} still failing - see Diagnostics (menu) for details.",
            ToastLength.Long)!.Show();
        RefreshStatus();
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

        if (_healthPermissionLauncher is null)
        {
            Toast.MakeText(this, "Health Connect permission request isn't available on this build - see Diagnostics (menu) for details.", ToastLength.Long)!.Show();
            return;
        }

        // Health Connect permissions are requested through Health Connect's own permission
        // screen via this ActivityResultContract - not a plain RequestPermissions call, which
        // silently does nothing on many devices/versions for this specific permission family.
        _healthPermissionLauncher.Launch(HealthConnectWriter.BuildRequiredPermissionSet());
    }

    /// <summary>Called back (via <see cref="HealthPermissionResultCallback"/>) once the user returns from the Health Connect permission screen.</summary>
    internal void OnHealthConnectPermissionResult(Java.Lang.Object? result)
    {
        bool granted = HealthConnectWriter.GrantedSetIncludesWritePermission(result);
        _lastHealthConnectPermissionResult = granted
            ? "Granted - Health Connect writes are allowed."
            : "Not granted. Open the button again, or grant it from Health Connect's own app settings.";

        RunOnUiThread(() =>
        {
            // A Toast is the wrong tool here: it disappears on its own after a few seconds and
            // this message is too long to reliably read (or even see in full) in that time. A
            // dialog stays up until dismissed, so the full text is always readable.
            new AlertDialog.Builder(this)!
                .SetTitle("Health Connect permission")!
                .SetMessage(_lastHealthConnectPermissionResult)!
                .SetPositiveButton("OK", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
            RefreshStatus();
        });
    }

    private sealed class HealthPermissionResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _owner;
        public HealthPermissionResultCallback(MainActivity owner) => _owner = owner;

        public void OnActivityResult(Java.Lang.Object? result) => _owner.OnHealthConnectPermissionResult(result);
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
        _deviceAddressesInOrder.Clear();
        _deviceListAdapter?.Clear();
        _tvScanEmpty.Text = "Scanning...";
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

            _tvScanEmpty.Text = _seenDevices.Count == 0
                ? "No BLE devices seen. Make sure the scale is powered on and try again."
                : "Tap a device below to fill in its address.";
        }, DebugScanDurationMs);
    }

    private void OnDeviceSeen(BluetoothDevice device, int rssi)
    {
        string name = device.Name ?? "(unnamed)";
        string display = $"{device.Address}\n{name}  rssi={rssi}";
        bool isNew = !_seenDevices.ContainsKey(device.Address);
        _seenDevices[device.Address] = display;

        RunOnUiThread(() =>
        {
            if (isNew)
            {
                _deviceAddressesInOrder.Add(device.Address);
                _deviceListAdapter?.Add(display);
            }
        });
    }

    private void OnDeviceRowClicked(object? sender, AdapterView.ItemClickEventArgs e)
    {
        if (e.Position < 0 || e.Position >= _deviceAddressesInOrder.Count)
            return;

        string address = _deviceAddressesInOrder[e.Position];
        _etMac.Text = address;
        _etMac.SetSelection(address.Length);
        // The MAC field is in a card further down the screen, below the device list - without
        // this, the field does get updated but the user has no reason to scroll down and see
        // that it worked. RequestFocus on a descendant of a ScrollView makes it auto-scroll to
        // bring that view into view.
        _etMac.RequestFocus();
        Toast.MakeText(this, $"Filled in {address} - see the MAC address field below.", ToastLength.Short)!.Show();
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

        // Collapse the setup section immediately once it actually succeeds, rather than waiting
        // for the next app launch to notice ScaleConfig.IsConfigured() - this is exactly the
        // "once configured, hide it" moment the user is acting on.
        if (result == ScaleScanRegistrar.RegisterResult.Success)
            _setupSectionVisible = false;

        RefreshStatus();
    }
}
