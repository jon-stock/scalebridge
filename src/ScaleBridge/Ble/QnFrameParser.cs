using System;

namespace ScaleBridge.Ble;

/// <summary>
/// Pure, Android-independent parser/builder for the "QN-Scale" vendor GATT protocol used by
/// Archonfit / Renpho / FitIndex / QN-family scales.
///
/// This is a direct C# port of the logic in openScale's current (2025) QNHandler.kt
/// (android_app/app/src/main/java/com/health/openscale/core/bluetooth/scales/QNHandler.kt),
/// NOT the older BluetoothQNScale.java referenced in Prompt.md Section 2. The Kotlin handler
/// supersedes the Java one and already contains fixes for the exact failure modes Prompt.md
/// Section 2 worried about (weight scale factor 10 vs 100, a protocol-type race condition that
/// caused the zero-weight bug, and an alternate "ES-30M" byte layout). See
/// docs/PROTOCOL_CONFIRMATION.md for the full write-up (Section 3 deliverable).
///
/// Kept free of any Android/BLE types so it can be reasoned about (and unit tested) in isolation
/// from the GATT plumbing in <see cref="QnScaleSession"/>.
/// </summary>
public static class QnFrameParser
{
    /// <summary>Vendor "epoch": seconds since 2000-01-01 00:00:00 UTC.</summary>
    public const long ScaleUnixTimestampOffset = 946_702_800L;

    public enum LiveWeightFormat
    {
        /// <summary>byte[3,4]=weight (big-endian), byte[5]=stable flag, bytes[6-9]=resistances.</summary>
        Original,

        /// <summary>byte[3]=unit, byte[4]=stable flag, bytes[5,6]=weight (big-endian), bytes[7-10]=resistances.</summary>
        Es30M,
    }

    public readonly struct LiveWeightFrame
    {
        public required bool Stable { get; init; }
        public required float WeightKg { get; init; }
        public required float Resistance1 { get; init; }
        public required float Resistance2 { get; init; }
        public required LiveWeightFormat Format { get; init; }
    }

    public readonly struct StoredMeasurementFrame
    {
        public required float WeightKg { get; init; }
        public required long RecordScaleSeconds { get; init; }
        public required float Resistance1 { get; init; }
        public required float Resistance2 { get; init; }
    }

    /// <summary>
    /// Parses a 0x10 "live weight" notification frame. Returns null if the frame is too short
    /// for either known layout. <paramref name="weightScaleFactor"/> is 100.0 or 10.0, as reported
    /// by the most recent 0x12 frame (see <see cref="ParseScaleInfoFrame"/>); defaults to 100.0
    /// before the first 0x12 frame arrives.
    /// </summary>
    public static LiveWeightFrame? TryParseLiveWeightFrame(byte[] data, float weightScaleFactor)
    {
        if (data.Length < 5)
            return null;

        int byte4 = data[4] & 0xFF;
        bool looksLikeEs30M = byte4 <= 0x02 && Math.Abs(weightScaleFactor - 10.0f) < 0.01f;

        bool stable;
        float raw, r1, r2;
        LiveWeightFormat format;

        if (looksLikeEs30M)
        {
            if (data.Length < 11)
                return null;

            int stableFlag = byte4;
            stable = stableFlag == 0x02 || stableFlag == 0x01;
            raw = U16Be(data[5], data[6]);
            r1 = U16Be(data[7], data[8]);
            r2 = U16Be(data[9], data[10]);
            format = LiveWeightFormat.Es30M;
        }
        else
        {
            if (data.Length < 10)
                return null;

            stable = data[5] == 1;
            raw = U16Be(data[3], data[4]);
            r1 = U16Be(data[6], data[7]);
            r2 = U16Be(data[8], data[9]);
            format = LiveWeightFormat.Original;
        }

        float weightKg = raw / weightScaleFactor;

        // Heuristic fallback ported from openScale: some "type 2" devices report with /10 even
        // before the 0x12 frame has arrived. If the value looks unreasonable, try /10 once more.
        if (weightKg <= 5f || weightKg >= 250f)
            weightKg /= 10.0f;

        return new LiveWeightFrame
        {
            Stable = stable,
            WeightKg = weightKg,
            Resistance1 = r1,
            Resistance2 = r2,
            Format = format,
        };
    }

    /// <summary>
    /// Parses a 0x23 "stored measurement" notification frame (returned after a 0x22 history
    /// query). Returns null if the frame is too short.
    /// </summary>
    public static StoredMeasurementFrame? TryParseStoredMeasurementFrame(byte[] data)
    {
        if (data.Length < 17)
            return null;

        float rawWeight = U16Be(data[10], data[11]);
        float weightKg = rawWeight / 100.0f;
        long recordScaleSeconds = U32Le(data[6], data[7], data[8], data[9]);
        float r1 = U16Le(data[13], data[14]);
        float r2 = U16Le(data[15], data[16]);

        return new StoredMeasurementFrame
        {
            WeightKg = weightKg,
            RecordScaleSeconds = recordScaleSeconds,
            Resistance1 = r1,
            Resistance2 = r2,
        };
    }

    /// <summary>
    /// Parses a 0x12 "scale info" frame. Returns the weight scale factor to divide raw values by
    /// (100.0 or 10.0), or null if the frame is too short to contain byte[10].
    /// </summary>
    public static float? ParseScaleInfoFrame(byte[] data)
    {
        if (data.Length <= 10)
            return null;

        return data[10] == 1 ? 100.0f : 10.0f;
    }

    /// <summary>Extracts the vendor "protocol type" byte (data[2]) used to echo back in our replies.</summary>
    public static byte? TryExtractProtocolType(byte[] data)
    {
        if (data.Length <= 2)
            return null;

        return data[2];
    }

    public static int Opcode(byte[] data) => data.Length > 0 ? data[0] & 0xFF : -1;

    /// <summary>Builds the 0x13 unit-configuration frame sent once the 0x12 scale-info frame has arrived.</summary>
    public static byte[] BuildUnitConfigFrame(byte protocolType, bool useLbUnit)
    {
        byte unitByte = useLbUnit ? (byte)0x02 : (byte)0x01;
        var cfg = new byte[] { 0x13, 0x09, protocolType, unitByte, 0x10, 0x00, 0x00, 0x00, 0x00 };
        cfg[^1] = Checksum(cfg, 0, cfg.Length - 1);
        return cfg;
    }

    /// <summary>Builds the 5-byte "current time" frame sent alongside/after unit configuration.</summary>
    public static byte[] BuildTimeMagicFrame(DateTimeOffset now)
    {
        int t = unchecked((int)(now.ToUnixTimeSeconds() - ScaleUnixTimestampOffset));
        return new byte[]
        {
            0x02,
            (byte)(t & 0xFF),
            (byte)((t >> 8) & 0xFF),
            (byte)((t >> 16) & 0xFF),
            (byte)((t >> 24) & 0xFF),
        };
    }

    /// <summary>Builds the 0x20 time-sync frame sent in reply to a 0x14 acknowledgement.</summary>
    public static byte[] BuildTimeSyncFrame(byte protocolType, DateTimeOffset now)
    {
        int t = unchecked((int)(now.ToUnixTimeSeconds() - ScaleUnixTimestampOffset));
        var msg = new byte[]
        {
            0x20, // Opcode
            0x08, // Length
            protocolType,
            (byte)(t & 0xFF),
            (byte)((t >> 8) & 0xFF),
            (byte)((t >> 16) & 0xFF),
            (byte)((t >> 24) & 0xFF),
            0x00, // Checksum placeholder
        };
        msg[^1] = Checksum(msg, 0, msg.Length - 2);
        return msg;
    }

    /// <summary>Builds the first of the two 0xA0 acknowledgement frames some QN sub-variants (e.g. ES-30M) require after a 0x21 frame.</summary>
    public static byte[] BuildAckFrame1()
    {
        var msg = new byte[] { 0xa0, 0x0d, 0x04, 0xfe, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        msg[^1] = Checksum(msg, 0, msg.Length - 2);
        return msg;
    }

    /// <summary>Builds the second of the two 0xA0 acknowledgement frames some QN sub-variants (e.g. ES-30M) require after a 0x21 frame.</summary>
    public static byte[] BuildAckFrame2()
    {
        var msg = new byte[] { 0xa0, 0x0d, 0x02, 0x01, 0x00, 0x08, 0x00, 0x21, 0x06, 0xb8, 0x04, 0x02, 0x00 };
        msg[^1] = Checksum(msg, 0, msg.Length - 2);
        return msg;
    }

    /// <summary>Builds the 0x22 stored-data query frame.</summary>
    public static byte[] BuildStoredDataQueryFrame(byte protocolType)
    {
        var msg = new byte[] { 0x22, 0x06, protocolType, 0x00, 0x03, 0x00 };
        msg[^1] = Checksum(msg, 0, msg.Length - 2);
        return msg;
    }

    public static byte Checksum(byte[] buf, int from, int toInclusive)
    {
        int s = 0;
        for (int i = from; i <= toInclusive; i++)
            s = (s + (buf[i] & 0xFF)) & 0xFF;
        return (byte)s;
    }

    private static float U16Be(byte a, byte b) => (((a & 0xFF) << 8) | (b & 0xFF));
    private static float U16Le(byte a, byte b) => ((a & 0xFF) | ((b & 0xFF) << 8));

    private static long U32Le(byte a, byte b, byte c, byte d) =>
        (a & 0xFFL) | ((b & 0xFFL) << 8) | ((c & 0xFFL) << 16) | ((d & 0xFFL) << 24);
}
