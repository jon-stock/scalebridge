using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Android.Util;
using ScaleBridge.Status;

namespace ScaleBridge.Ble;

/// <summary>
/// Owns one GATT connection to the scale and drives the QN-Scale handshake/notification state
/// machine described in docs/PROTOCOL_CONFIRMATION.md. This is a direct C# port of the sequencing
/// in openScale's QNHandler.kt: discover services -> subscribe to notifications -> wait for the
/// 0x12 "scale info" frame (which reveals whether raw weight needs /100 or /10) -> send unit +
/// time configuration -> handle the 0x14/0x21 acknowledgement dance some sub-variants require ->
/// capture the first stable 0x10 live-weight frame (or, failing that, a recent 0x23 stored
/// measurement) -> report it and stop.
///
/// GATT only allows one outstanding operation (read/write/descriptor-write) at a time, so writes
/// and notification-enable descriptor writes are serialized through a small internal queue.
/// </summary>
public sealed class QnScaleSession : BluetoothGattCallback
{
    private const string LogTag = "ScaleBridge.Qn";
    private const int MaxStoredDataQueryAttempts = 10;
    private const long StoredDataRetryDelayMs = 5_000;

    // Android's BLE stack frequently fails an initial connectGatt()/service-discovery attempt
    // with a generic, non-actionable status (most commonly GATT_ERROR / status 133) for
    // transient reasons - a stale GATT resource left over from a previous attempt, the device
    // briefly out of range, or a radio/timing hiccup - that a fresh attempt shortly afterwards
    // usually clears. Previously any such failure before a stable connection was ever reached
    // was treated as a silent, non-error "disconnected" and the whole sync attempt was simply
    // abandoned with no retry and no user-visible notification at all. MaxConnectAttempts
    // includes the initial attempt; ConnectRetryDelaysMs holds the backoff before each retry
    // (index 0 = delay before attempt 2, etc., with the last entry reused for any further retry).
    private const int MaxConnectAttempts = 3;
    private static readonly long[] ConnectRetryDelaysMs = { 1_000, 3_000 };

    public event Action<string>? StatusChanged;
    public event Action<double>? WeightCaptured;
    public event Action<string>? Failed;
    public event Action? Disconnected;

    private readonly Handler _mainHandler = new(Looper.MainLooper!);
    private readonly Queue<Action> _opQueue = new();
    private bool _opInFlight;

    private BluetoothGatt? _gatt;

    private BluetoothGattCharacteristic? _chrT1NotifyWeightTime;
    private BluetoothGattCharacteristic? _chrT1IndicateMisc;
    private BluetoothGattCharacteristic? _chrT1WriteConfig;
    private BluetoothGattCharacteristic? _chrT1WriteTime;
    private BluetoothGattCharacteristic? _chrT2NotifyWeightTime;
    private BluetoothGattCharacteristic? _chrT2WriteShared;

    private bool _hasPublishedForThisSession;
    private float _weightScaleFactor = 100.0f;
    private byte _seenProtocolType;
    private bool _hasReceivedProtocolType;
    private bool _isConnected;
    private int _historyQueryAttempts;
    private long _sessionStartedScaleSeconds;

    // Connect-phase retry state (see MaxConnectAttempts/ConnectRetryDelaysMs above).
    private Context? _connectContext;
    private BluetoothDevice? _connectDevice;
    private int _connectAttempts;
    private bool _hasEverConnected;
    private bool _closed;

    public void Connect(Context context, BluetoothDevice device)
    {
        _connectContext = context;
        _connectDevice = device;
        _connectAttempts = 0;
        _hasEverConnected = false;
        _closed = false;
        AttemptConnect();
    }

    private void AttemptConnect()
    {
        if (_closed || _connectDevice is null)
            return;

        // Belt-and-braces: always release any previous GATT resource before creating a new one.
        // A stale, not-fully-closed BluetoothGatt from a prior attempt reusing the same underlying
        // connection slot is one of the most common real-world causes of status 133.
        CleanupGattForRetry();

        _connectAttempts++;
        Log.Info(LogTag, $"Connecting to {_connectDevice.Address} (attempt {_connectAttempts}/{MaxConnectAttempts})...");
        try
        {
            _gatt = _connectDevice.ConnectGatt(_connectContext, false, this, BluetoothTransports.Le);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"connectGatt threw: {ex.Message}");
            _gatt = null;
        }

        if (_gatt is null)
            HandleConnectFailure("connectGatt returned null");
    }

    /// <summary>
    /// Called for any failure before a connection has ever been fully established for the current
    /// connect attempt (connectGatt returning null, a connect-time disconnect/status-133-style
    /// failure, or a service-discovery failure). Retries with backoff up to
    /// <see cref="MaxConnectAttempts"/> total attempts; only once that budget is exhausted is this
    /// surfaced as a real failure via <see cref="Failed"/> (previously any single such failure was
    /// silently swallowed with no retry and no user-visible notification).
    /// </summary>
    private void HandleConnectFailure(string reason)
    {
        if (_closed)
            return;

        _isConnected = false;
        _hasEverConnected = false;
        CleanupGattForRetry();

        if (_connectAttempts < MaxConnectAttempts)
        {
            long delay = ConnectRetryDelaysMs[Math.Min(_connectAttempts - 1, ConnectRetryDelaysMs.Length - 1)];
            Log.Warn(LogTag, $"Connect attempt {_connectAttempts}/{MaxConnectAttempts} failed ({reason}); retrying in {delay}ms.");
            StatusChanged?.Invoke($"Connection attempt {_connectAttempts} failed, retrying...");
            _mainHandler.PostDelayed(AttemptConnect, delay);
        }
        else
        {
            Log.Error(LogTag, $"Giving up after {_connectAttempts} connect attempts ({reason}).");
            Failed?.Invoke($"Could not connect to the scale after {_connectAttempts} attempts ({reason}).");
        }
    }

    private void CleanupGattForRetry()
    {
        try
        {
            _gatt?.Close();
        }
        catch (Java.Lang.Exception ex)
        {
            Log.Warn(LogTag, $"Error closing GATT before retry: {ex.Message}");
        }
        _gatt = null;
    }

    public void Close()
    {
        _closed = true;
        _mainHandler.RemoveCallbacksAndMessages(null);
        try
        {
            _gatt?.Disconnect();
            _gatt?.Close();
        }
        catch (Java.Lang.Exception ex)
        {
            Log.Warn(LogTag, $"Error closing GATT: {ex.Message}");
        }
        _gatt = null;
    }

    // ---- Connection lifecycle ---------------------------------------------------

    // Every public BluetoothGattCallback override in this class is a thin try/catch wrapper
    // around a "Core" method that does the real work - see HandleUnexpectedCallbackException's
    // doc comment for why: these run on a Binder thread pool thread with nothing else above them
    // in the managed call stack, so an unexpected exception here would otherwise take down the
    // whole app process instead of just this one connection attempt.
    public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
    {
        try
        {
            OnConnectionStateChangeCore(gatt, status, newState);
        }
        catch (Exception ex)
        {
            HandleUnexpectedCallbackException(nameof(OnConnectionStateChange), ex);
        }
    }

    private void OnConnectionStateChangeCore(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
    {
        if (_closed)
            return;

        if (newState == ProfileState.Connected)
        {
            Log.Info(LogTag, "GATT connected; discovering services.");
            _isConnected = true;
            _hasEverConnected = true;
            _connectAttempts = 0;
            gatt?.DiscoverServices();
        }
        else if (newState == ProfileState.Disconnected)
        {
            _isConnected = false;
            if (!_hasEverConnected)
            {
                // A disconnect before we ever reached the Connected state is a connect-time
                // failure (e.g. the classic status-133 GATT_ERROR), not a normal end-of-session
                // disconnect - retry rather than giving up immediately (see HandleConnectFailure;
                // this also raises Failed - CrashLog/StatusStore/notification - once the retry
                // budget is exhausted, rather than every single attempt being silent).
                HandleConnectFailure($"status={status} ({(int)status})");
            }
            else
            {
                Log.Info(LogTag, $"GATT disconnected (status={status}).");
                Disconnected?.Invoke();
            }
        }
    }

    public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
    {
        try
        {
            OnServicesDiscoveredCore(gatt, status);
        }
        catch (Exception ex)
        {
            HandleUnexpectedCallbackException(nameof(OnServicesDiscovered), ex);
        }
    }

    private void OnServicesDiscoveredCore(BluetoothGatt? gatt, GattStatus status)
    {
        if (_closed)
            return;

        if (gatt is null || status != GattStatus.Success)
        {
            // Also retried, for the same reasons as a connect-time failure above - a failed
            // service discovery leaves the GATT connection unusable, so a fresh connectGatt is
            // needed rather than just giving up.
            HandleConnectFailure($"service discovery failed (status={status})");
            return;
        }

        ResetSessionState();

        var svcT1 = gatt.GetService(ScaleGattUuids.ServiceT1);
        _chrT1NotifyWeightTime = svcT1?.GetCharacteristic(ScaleGattUuids.CharT1NotifyWeightTime);
        _chrT1IndicateMisc = svcT1?.GetCharacteristic(ScaleGattUuids.CharT1IndicateMisc);
        _chrT1WriteConfig = svcT1?.GetCharacteristic(ScaleGattUuids.CharT1WriteConfig);
        _chrT1WriteTime = svcT1?.GetCharacteristic(ScaleGattUuids.CharT1WriteTime);

        var svcT2 = gatt.GetService(ScaleGattUuids.ServiceT2);
        _chrT2NotifyWeightTime = svcT2?.GetCharacteristic(ScaleGattUuids.CharT2NotifyWeightTime);
        _chrT2WriteShared = svcT2?.GetCharacteristic(ScaleGattUuids.CharT2WriteShared);

        if (_chrT1NotifyWeightTime is null && _chrT2NotifyWeightTime is null)
        {
            Failed?.Invoke("Neither known QN-Scale notify characteristic (0xFFE1/0xFFF1) was found on this device.");
            return;
        }

        // Best-effort device identification reads; failures here are logged but non-fatal.
        EnqueueRead(ScaleGattUuids.GenericAccessService, ScaleGattUuids.DeviceNameCharacteristic);
        EnqueueRead(ScaleGattUuids.DeviceInformationService, ScaleGattUuids.ManufacturerNameCharacteristic);
        EnqueueRead(ScaleGattUuids.DeviceInformationService, ScaleGattUuids.ModelNumberCharacteristic);
        EnqueueRead(ScaleGattUuids.DeviceInformationService, ScaleGattUuids.FirmwareRevisionCharacteristic);
        EnqueueRead(ScaleGattUuids.DeviceInformationService, ScaleGattUuids.SoftwareRevisionCharacteristic);

        EnqueueEnableNotify(_chrT1NotifyWeightTime, indicate: false);
        EnqueueEnableNotify(_chrT1IndicateMisc, indicate: true);
        EnqueueEnableNotify(_chrT2NotifyWeightTime, indicate: false);

        // IMPORTANT (mirrors QNHandler.kt): do NOT send configuration yet. We must wait for the
        // 0x12 frame to learn the correct weight scale factor first - sending configuration too
        // early was the root cause of a protocol-type race that produced the zero-weight bug
        // described in Prompt.md Section 2.
        StatusChanged?.Invoke("Connected - waiting for the scale to be stepped on");
    }

    private void ResetSessionState()
    {
        _hasPublishedForThisSession = false;
        _weightScaleFactor = 100.0f;
        _seenProtocolType = 0;
        _hasReceivedProtocolType = false;
        HasReceivedAnyVendorData = false;
        _historyQueryAttempts = 0;
        _sessionStartedScaleSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - QnFrameParser.ScaleUnixTimestampOffset;
        _mainHandler.RemoveCallbacksAndMessages(null);

        // Defensive: not currently reachable (this only runs once per successful service
        // discovery, and nothing today re-enters service discovery on the same instance after
        // ops have been queued), but the op queue is state tied to one specific GATT connection -
        // leaving stale entries/an incorrectly-true _opInFlight around for a fresh session would
        // reproduce the same "queue never advances" class of bug as RunGattOpOrAdvance guards
        // against, just via a different path, if this class's connect/retry flow is ever changed.
        _opQueue.Clear();
        _opInFlight = false;
    }

    // ---- GATT op queue (only one outstanding GATT operation at a time is allowed) ----------

    private void EnqueueRead(Java.Util.UUID serviceUuid, Java.Util.UUID characteristicUuid)
    {
        var characteristic = _gatt?.GetService(serviceUuid)?.GetCharacteristic(characteristicUuid);
        if (characteristic is null)
            return;

        Enqueue(() => RunGattOpOrAdvance(
            () => _gatt?.ReadCharacteristic(characteristic) ?? false,
            $"ReadCharacteristic({characteristic.Uuid})"));
    }

    private void EnqueueEnableNotify(BluetoothGattCharacteristic? characteristic, bool indicate)
    {
        if (characteristic is null || _gatt is null)
            return;

        var gatt = _gatt;
        Enqueue(() =>
        {
            gatt.SetCharacteristicNotification(characteristic, true);
            var descriptor = characteristic.GetDescriptor(ScaleGattUuids.ClientCharacteristicConfig);
            if (descriptor is null)
            {
                RunNext();
                return;
            }

            // Bound as IList<byte> rather than byte[]; SetValue needs the array form.
            var enableValue = indicate
                ? BluetoothGattDescriptor.EnableIndicationValue
                : BluetoothGattDescriptor.EnableNotificationValue;
            descriptor.SetValue(enableValue?.ToArray());
            RunGattOpOrAdvance(() => gatt.WriteDescriptor(descriptor), $"WriteDescriptor({characteristic.Uuid})");
        });
    }

    private void EnqueueWrite(BluetoothGattCharacteristic? characteristic, byte[] payload)
    {
        if (characteristic is null || _gatt is null)
            return;

        var gatt = _gatt;
        Enqueue(() =>
        {
            characteristic.WriteType = GattWriteType.Default;
            characteristic.SetValue(payload);
            RunGattOpOrAdvance(() => gatt.WriteCharacteristic(characteristic), $"WriteCharacteristic({characteristic.Uuid})");
        });
    }

    /// <summary>
    /// Runs a GATT read/write/descriptor-write call and checks its return value. These Android
    /// APIs return a <c>bool</c> indicating whether the operation was actually *accepted* by the
    /// native Bluetooth stack - if the stack rejects it (busy, a transient timing hiccup, a stale
    /// resource), it returns <c>false</c> and, critically, no completion callback
    /// (<see cref="OnCharacteristicRead"/>/<see cref="OnCharacteristicWrite"/>/
    /// <see cref="OnDescriptorWrite"/>) will ever arrive for it. Previously that return value was
    /// ignored at every call site: <see cref="RunNext"/> had already marked an operation
    /// in-flight, so a rejected call meant the queue would wait forever for a callback that was
    /// never coming - silently stalling every subsequent queued operation (notification
    /// subscriptions, unit/time configuration, handshake acknowledgements, stored-data queries)
    /// for the rest of that connection, indistinguishable from the scale simply not being stepped
    /// on. This is the same class of bug as the scan cooldown fix: state was being driven by
    /// "the call was made" rather than "what actually happened". On a rejection we log it and
    /// immediately advance the queue ourselves instead of waiting for a callback that will never
    /// fire - losing just that one operation rather than every operation queued after it.
    /// </summary>
    private void RunGattOpOrAdvance(Func<bool> op, string description)
    {
        bool accepted = op();
        if (!accepted)
        {
            Log.Warn(LogTag, $"GATT stack rejected {description} (returned false); skipping and advancing the queue.");
            RunNext();
        }
    }

    private void Enqueue(Action operation)
    {
        _opQueue.Enqueue(operation);
        if (!_opInFlight)
            RunNext();
    }

    private void RunNext()
    {
        if (_opQueue.Count == 0)
        {
            _opInFlight = false;
            return;
        }

        _opInFlight = true;
        var op = _opQueue.Dequeue();

        // Every queued GATT operation ultimately runs from inside a BluetoothGattCallback
        // override, invoked on a Binder thread pool thread with no managed try/catch anywhere
        // above it in the call stack - an unexpected exception here (a binding-generator
        // surprise, an unusual real-device GATT state, or a bug not yet hit in this queue's more
        // than a year of paths) would otherwise propagate as a genuine unhandled exception,
        // taking down the entire app process rather than just failing this one sync attempt. See
        // HandleUnexpectedCallbackException's own doc comment for the full rationale - this is
        // one of several call sites across this class deliberately hardened against exactly the
        // "worked fine for days, then a different unhandled exception" failure pattern.
        try
        {
            op();
        }
        catch (Exception ex)
        {
            HandleUnexpectedCallbackException(nameof(RunNext), ex);
        }
    }

    /// <summary>
    /// Logs, best-effort records to <see cref="CrashLog"/>, and converts an unexpected exception
    /// from inside any GATT callback into an ordinary, recoverable <see cref="Failed"/> event
    /// instead of letting it propagate as a genuine unhandled exception. Every override below
    /// runs on a Binder thread pool thread with no other managed exception handling above it in
    /// the call stack - previously, any bug here (however rare) crashed the entire app process,
    /// not just this one connection attempt. This is deliberately broad (catches
    /// <see cref="Exception"/>, not just expected Java/Bluetooth exception types): the whole
    /// point is to convert *unexpected* failures - the ones that, by definition, weren't
    /// anticipated specifically enough to catch narrowly - into a diagnosable, contained failure
    /// of just this sync attempt.
    /// </summary>
    private void HandleUnexpectedCallbackException(string where, Exception ex)
    {
        Log.Error(LogTag, $"Unexpected exception in {where}: {ex}");

        if (_connectContext is not null)
        {
            try
            {
                CrashLog.Record(_connectContext, ex);
            }
            catch
            {
                // As in CrashLog.Record itself: never let crash-logging throw from inside a
                // handler that is itself already recovering from an exception.
            }
        }

        Failed?.Invoke($"Unexpected internal error ({where}): {ex.GetType().Name}: {ex.Message}");
    }

    public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status) => RunNext();
    public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status) => RunNext();

    // Deliberately overriding only the pre-API-33 read/notify callbacks: Android's own
    // BluetoothGattCallback default-implements the newer byte[]-carrying overloads by calling
    // these, so this single override works unchanged on API 26 through 34+.
    public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
    {
        try
        {
            if (status == GattStatus.Success && characteristic is not null)
            {
                var bytes = characteristic.GetValue();
                if (bytes is not null)
                    Log.Debug(LogTag, $"Read {characteristic.Uuid}: {ToHex(bytes)} / \"{System.Text.Encoding.ASCII.GetString(bytes)}\"");
            }
        }
        catch (Exception ex)
        {
            // Logged/recorded, but deliberately not re-thrown into RunNext's own try/catch below:
            // these best-effort device-identification reads are diagnostic only (see
            // OnServicesDiscoveredCore's comment above EnqueueRead) and must never abort the
            // queue or raise Failed over a logging-only failure.
            Log.Warn(LogTag, $"Non-fatal error handling characteristic read: {ex}");
        }

        RunNext();
    }

    public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
    {
        try
        {
            OnCharacteristicChangedCore(characteristic);
        }
        catch (Exception ex)
        {
            HandleUnexpectedCallbackException(nameof(OnCharacteristicChanged), ex);
        }
    }

    private void OnCharacteristicChangedCore(BluetoothGattCharacteristic? characteristic)
    {
        var data = characteristic?.GetValue();
        if (characteristic is null || data is null)
            return;

        if (characteristic.Uuid?.Equals(ScaleGattUuids.CharT1NotifyWeightTime) == true ||
            characteristic.Uuid?.Equals(ScaleGattUuids.CharT2NotifyWeightTime) == true)
        {
            HandleVendorPacket(data);
        }
        else if (characteristic.Uuid?.Equals(ScaleGattUuids.CharT1IndicateMisc) == true)
        {
            Log.Debug(LogTag, $"Indicate misc: {ToHex(data)}");
        }
        else
        {
            Log.Debug(LogTag, $"Unhandled notify from {characteristic.Uuid}: {ToHex(data)}");
        }
    }

    // ---- Vendor protocol handling (ported from QNHandler.kt) --------------------------------

    /// <summary>
    /// True once at least one vendor notify frame has been received for this session - lets
    /// <see cref="Ble.ScaleConnectionService"/>'s overall timeout tell "the scale was never
    /// stepped on" (no vendor traffic at all - genuinely informational, not an error) apart from
    /// "the handshake started but stalled before ever producing a stable weight" (real vendor
    /// frames were seen, so this is worth surfacing as an actual, diagnosable error rather than
    /// staying completely silent).
    /// </summary>
    public bool HasReceivedAnyVendorData { get; private set; }

    private void HandleVendorPacket(byte[] data)
    {
        HasReceivedAnyVendorData = true;

        if (data.Length < 3)
            return;

        if (_seenProtocolType == 0 && data.Length > 2)
        {
            _seenProtocolType = QnFrameParser.TryExtractProtocolType(data) ?? 0;
            Log.Debug(LogTag, $"Captured protocol type 0x{_seenProtocolType:X2}");
        }

        int opcode = QnFrameParser.Opcode(data);
        switch (opcode)
        {
            case 0x10:
                HandleLiveWeightFrame(data);
                break;

            case 0x14:
                Log.Debug(LogTag, "Received 0x14 ack; sending 0x20 time sync.");
                var timeSync = QnFrameParser.BuildTimeSyncFrame(_seenProtocolType, DateTimeOffset.UtcNow);
                WriteToPreferredT2ThenT1(timeSync);
                break;

            case 0x12:
                HandleScaleInfoFrame(data);
                break;

            case 0x21:
                Log.Debug(LogTag, "Received 0x21; sending the two required 0xA0 acknowledgements.");
                WriteToPreferredT2ThenT1(QnFrameParser.BuildAckFrame1());
                WriteToPreferredT2ThenT1(QnFrameParser.BuildAckFrame2());
                SendStoredDataQuery("initial 0x21 handshake");
                break;

            case 0x23:
                HandleStoredMeasurementFrame(data);
                break;

            case 0xA1:
            case 0xA3:
                Log.Debug(LogTag, $"Received 0x{opcode:X2} acknowledgement.");
                break;

            default:
                Log.Debug(LogTag, $"Unhandled opcode 0x{opcode:X2}: {ToHex(data)}");
                break;
        }
    }

    private void HandleLiveWeightFrame(byte[] data)
    {
        Log.Debug(LogTag, $"Raw live-weight notify: {ToHex(data)}");

        var frame = QnFrameParser.TryParseLiveWeightFrame(data, _weightScaleFactor);
        if (frame is null)
            return;

        Log.Debug(LogTag,
            $"weight={frame.Value.WeightKg} kg stable={frame.Value.Stable} format={frame.Value.Format} (scaleFactor={_weightScaleFactor})");

        if (!frame.Value.Stable || _hasPublishedForThisSession)
            return;

        if (frame.Value.WeightKg > 0f)
            Publish(frame.Value.WeightKg, "live");
    }

    private void HandleStoredMeasurementFrame(byte[] data)
    {
        Log.Debug(LogTag, $"Stored measurement frame (0x23): {ToHex(data)}");

        if (_hasPublishedForThisSession)
            return;

        var frame = QnFrameParser.TryParseStoredMeasurementFrame(data);
        if (frame is null)
        {
            ScheduleStoredDataRetry("stored frame too short");
            return;
        }

        if (frame.Value.WeightKg <= 5f || frame.Value.WeightKg >= 300f)
        {
            ScheduleStoredDataRetry("weight out of range");
            return;
        }

        // Reject records saved before this connection started (i.e. from a previous session),
        // per Prompt.md Section 4 requirement 3: never record a stale/idle reading as "now".
        const long maxStoredRecordAgeBeforeSessionSeconds = 90;
        if (frame.Value.RecordScaleSeconds + maxStoredRecordAgeBeforeSessionSeconds < _sessionStartedScaleSeconds)
        {
            ScheduleStoredDataRetry("stale stored record");
            return;
        }

        Publish(frame.Value.WeightKg, "stored");
    }

    private void HandleScaleInfoFrame(byte[] data)
    {
        var factor = QnFrameParser.ParseScaleInfoFrame(data);
        if (factor is null)
            return;

        _weightScaleFactor = factor.Value;
        Log.Debug(LogTag, $"weightScaleFactor set to {_weightScaleFactor} from 0x12 frame.");

        if (!_hasReceivedProtocolType)
        {
            _hasReceivedProtocolType = true;
            SendConfigurationCommands();
        }
    }

    private void SendConfigurationCommands()
    {
        // Always configure the scale's own display to kg: the raw weight value in the 0x10/0x23
        // frames is independent of this unit byte (it is always converted to kg from
        // weightScaleFactor before we ever look at it), so this only affects what the scale's own
        // screen shows - Prompt.md Section 4 requirement 4 (normalise to kg for storage).
        var cfg = QnFrameParser.BuildUnitConfigFrame(_seenProtocolType, useLbUnit: false);
        EnqueueWrite(_chrT1WriteConfig, cfg);
        EnqueueWrite(_chrT2WriteShared, cfg);

        var timeMagic = QnFrameParser.BuildTimeMagicFrame(DateTimeOffset.UtcNow);
        EnqueueWrite(_chrT1WriteTime, timeMagic);
        EnqueueWrite(_chrT2WriteShared, timeMagic);
    }

    private void SendStoredDataQuery(string reason)
    {
        if (!_isConnected || _hasPublishedForThisSession)
            return;

        if (_historyQueryAttempts >= MaxStoredDataQueryAttempts)
        {
            Log.Debug(LogTag, $"Stored data query limit reached after {reason}.");
            return;
        }

        _historyQueryAttempts++;
        var query = QnFrameParser.BuildStoredDataQueryFrame(_seenProtocolType);
        Log.Debug(LogTag, $"Sending stored data query attempt {_historyQueryAttempts}/{MaxStoredDataQueryAttempts} after {reason}.");
        WriteToPreferredT2ThenT1(query);
    }

    private void ScheduleStoredDataRetry(string reason)
    {
        if (!_isConnected || _hasPublishedForThisSession)
            return;

        if (_historyQueryAttempts >= MaxStoredDataQueryAttempts)
            return;

        _mainHandler.PostDelayed(() => SendStoredDataQuery($"retry after {reason}"), StoredDataRetryDelayMs);
    }

    private void WriteToPreferredT2ThenT1(byte[] payload)
    {
        if (_chrT2WriteShared is not null)
            EnqueueWrite(_chrT2WriteShared, payload);
        else if (_chrT1WriteConfig is not null)
            EnqueueWrite(_chrT1WriteConfig, payload);
    }

    private void Publish(float weightKg, string source)
    {
        _hasPublishedForThisSession = true;
        Log.Info(LogTag, $"Publishing {source} weight={weightKg} kg.");
        StatusChanged?.Invoke($"Captured {source} weight: {weightKg:0.0} kg");
        WeightCaptured?.Invoke(weightKg);
    }

    private static string ToHex(byte[] data) => Convert.ToHexString(data);
}
