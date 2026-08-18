using Java.Util;

namespace ScaleBridge.Ble;

/// <summary>
/// GATT service/characteristic UUIDs for the QN-Scale vendor protocol, expanded from their
/// 16-bit short form using the standard Bluetooth Base UUID. Two near-identical layouts exist
/// in the wild ("Type 1" on 0xFFE0.. and "Type 2" on 0xFFF0..); we subscribe to both and let
/// <see cref="QnScaleSession"/> figure out at runtime which one this specific scale actually uses.
/// Ported from openScale's QNHandler.kt - see docs/PROTOCOL_CONFIRMATION.md.
/// </summary>
public static class ScaleGattUuids
{
    public static UUID Uuid16(int shortUuid) => Expand(shortUuid);

    private static UUID Expand(int shortUuid) =>
        UUID.FromString($"0000{shortUuid:X4}-0000-1000-8000-00805f9b34fb");

    // Standard GATT services used for device identification during onConnected().
    public static readonly UUID GenericAccessService = Expand(0x1800);
    public static readonly UUID DeviceNameCharacteristic = Expand(0x2A00);
    public static readonly UUID DeviceInformationService = Expand(0x180A);
    public static readonly UUID ManufacturerNameCharacteristic = Expand(0x2A29);
    public static readonly UUID ModelNumberCharacteristic = Expand(0x2A24);
    public static readonly UUID FirmwareRevisionCharacteristic = Expand(0x2A26);
    public static readonly UUID SoftwareRevisionCharacteristic = Expand(0x2A28);

    // Type 1 (FFE0..FFE5)
    public static readonly UUID ServiceT1 = Expand(0xFFE0);
    public static readonly UUID CharT1NotifyWeightTime = Expand(0xFFE1); // notify: weight/time/resistances
    public static readonly UUID CharT1IndicateMisc = Expand(0xFFE2);     // indicate: misc ack
    public static readonly UUID CharT1WriteConfig = Expand(0xFFE3);      // write: unit config
    public static readonly UUID CharT1WriteTime = Expand(0xFFE4);       // write: time sync

    // Type 2 (FFF0..FFF2)
    public static readonly UUID ServiceT2 = Expand(0xFFF0);
    public static readonly UUID CharT2NotifyWeightTime = Expand(0xFFF1); // notify: weight/time/resistances
    public static readonly UUID CharT2WriteShared = Expand(0xFFF2);     // write: unit + time on T2

    // Vendor-specific service seen on newer QN firmware; only used for device-family detection
    // during scanning, never read/written directly.
    public static readonly UUID VendorAe00Service = Expand(0xAE00);

    // Standard Client Characteristic Configuration Descriptor, needed to enable notifications.
    public static readonly UUID ClientCharacteristicConfig = Expand(0x2902);
}
