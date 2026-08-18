using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Android.Util;

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

    public void Connect(Context context, BluetoothDevice device)
    {
        Log.Info(LogTag, $"Connecting to {device.Address}...");
        _gatt = device.ConnectGatt(context, false, this, BluetoothTransports.Le);
    }

    public void Close()
    {
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

    public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
    {
        if (newState == ProfileState.Connected)
        {
            Log.Info(LogTag, "GATT connected; discovering services.");
            _isConnected = true;
            gatt?.DiscoverServices();
        }
        else if (newState == ProfileState.Disconnected)
        {
            Log.Info(LogTag, $"GATT disconnected (status={status}).");
            _isConnected = false;
            Disconnected?.Invoke();
        }
    }

    public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
    {
        if (gatt is null || status != GattStatus.Success)
        {
            Failed?.Invoke($"Service discovery failed (status={status}).");
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
        _historyQueryAttempts = 0;
        _sessionStartedScaleSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - QnFrameParser.ScaleUnixTimestampOffset;
        _mainHandler.RemoveCallbacksAndMessages(null);
    }

    // ---- GATT op queue (only one outstanding GATT operation at a time is allowed) ----------

    private void EnqueueRead(Java.Util.UUID serviceUuid, Java.Util.UUID characteristicUuid)
    {
        var characteristic = _gatt?.GetService(serviceUuid)?.GetCharacteristic(characteristicUuid);
        if (characteristic is null)
            return;

        Enqueue(() => _gatt?.ReadCharacteristic(characteristic));
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

            descriptor.SetValue(indicate
                ? BluetoothGattDescriptor.EnableIndicationValue
                : BluetoothGattDescriptor.EnableNotificationValue);
            gatt.WriteDescriptor(descriptor);
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
            gatt.WriteCharacteristic(characteristic);
        });
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
        op();
    }

    public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status) => RunNext();
    public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status) => RunNext();

    // Deliberately overriding only the pre-API-33 read/notify callbacks: Android's own
    // BluetoothGattCallback default-implements the newer byte[]-carrying overloads by calling
    // these, so this single override works unchanged on API 26 through 34+.
    public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
    {
        if (status == GattStatus.Success && characteristic is not null)
        {
            var bytes = characteristic.GetValue();
            if (bytes is not null)
                Log.Debug(LogTag, $"Read {characteristic.Uuid}: {ToHex(bytes)} / \"{System.Text.Encoding.ASCII.GetString(bytes)}\"");
        }

        RunNext();
    }

    public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
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

    private void HandleVendorPacket(byte[] data)
    {
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
