# Developer Prompt: Archonfit BLE Scale → Health Connect Sync App

## 1. Objective

Build a small, single-purpose Android application in **C# (.NET for Android / .NET MAUI targeting Android only)** that:

1. Automatically detects and connects to a specific Archonfit Bluetooth (BLE) bathroom scale as soon as it powers on — no manual "connect" button press.
2. Captures the final, stabilised weight reading from the scale (not intermediate/in-progress values, and not the scale's initial 0 kg idle packet).
3. Writes that weight reading automatically to **Android Health Connect** with no user interaction required.

This is a personal, one-off utility for a single user and a single known device. It does not need to be generic, does not need a UI beyond minimal status/debug output, and will not be distributed via the Play Store — it will be built as a signed APK and sideloaded onto one phone.

## 2. Background / What We Already Know

- The scale is sold as "Archonfit." Its original companion app no longer works.
- Using the open-source app **openScale** (github.com/oliexdev/openScale) as a diagnostic tool, the scale was identified as matching openScale's **"QN-Scale"** device driver family. This family covers a number of rebadged/OEM scales (Renpho, FitIndex, Kamtron, Elektra, Korehealth Korescale, and others) that share a common chipset and protocol.
- QN-Scale devices are **connection-based, not broadcast-based**: the phone must open a GATT connection and perform a short command handshake with the scale before it sends weight data. This is unlike simpler scales that just broadcast weight in BLE advertisement packets.
- openScale's driver for this family lives at:
  `android_app/app/src/main/java/com/health/openscale/core/bluetooth/BluetoothQNScale.java`
  in the openScale GitHub repository (GPLv3 licensed). This file contains the known service/characteristic UUIDs and the command sequence used to initiate a reading, and should be used as the primary reference for the GATT protocol, rather than reverse-engineering from scratch.
- **Known issue:** openScale currently records a weight of 0 for this scale. Other QN-Scale sub-variants (e.g. Renpho Elis 1, Korehealth Korescale G2) have documented compatibility issues in openScale's GitHub issue tracker, including mismatched characteristic UUIDs and parsing errors — suggesting Archonfit is a close-but-not-identical clone of the scale openScale's driver was originally written for. The zero-weight bug is most likely one of:
  - The real weight is present in the notification payload but at a different byte offset/scale-factor than openScale assumes, or
  - The app is capturing the scale's initial idle/0 kg packet instead of waiting for the final stabilised reading (a stability flag bit, similar to Xiaomi Mi Scale's protocol, likely marks this), or
  - A slightly different init/handshake sequence is required before the scale will send live data.

## 3. Required First Task: Confirm the Protocol Before Building

**Do not write the final parsing logic on assumption.** Before implementing the weight-capture logic, the developer must:

1. Capture real BLE traffic between the Archonfit scale and openScale (or the original Archonfit app, if it can be made to run even partially) using either:
   - Android's built-in Bluetooth HCI snoop log (Developer Options → "Enable Bluetooth HCI snoop log"), or
   - openScale's own file logging feature (Settings → General → File logging), which logs raw GATT commands/notifications.
2. Analyse the captured log against `BluetoothQNScale.java`'s expected byte layout to identify exactly where it diverges (offset, scale factor, stability flag, or handshake step).
3. Confirm the corrected byte layout with a short written summary (a few lines is fine) before proceeding to implementation, so the approach can be sanity-checked.

This step avoids building an app around incorrect assumptions and re-doing work later.

## 4. Functional Requirements

| # | Requirement | Notes |
|---|---|---|
| 1 | Detect the scale automatically when it powers on | Use a BLE scan registered with a system-level callback (e.g. `BluetoothLeScanner` with a `PendingIntent`-backed scan filter on Android) so the OS can wake the app on a matching advertisement, rather than running a continuous foreground scan. Filter on the scale's specific MAC address and/or advertised name. |
| 2 | Connect via GATT and perform the QN-Scale handshake | Reuse the UUIDs/command sequence from `BluetoothQNScale.java`, adjusted per the findings in Section 3. |
| 3 | Capture only the final, stable weight | Do not record intermediate in-progress values or the initial idle/0 kg packet. Identify and use the stability flag bit if present, similar to how Xiaomi Mi Scale marks a "stabilized" bit alongside a "weight removed" bit. |
| 4 | Convert to correct units | Confirm whether the scale reports kg or lb and the correct scale factor (commonly ÷100 or ÷200 in similar protocols), and normalise to kg for storage. |
| 5 | Write to Health Connect | Use the `androidx.health.connect:connect-client` library (via .NET Android bindings, or a small platform-specific binding layer if no direct NuGet package is suitable) to insert a `WeightRecord` with the captured value and timestamp. Request the necessary Health Connect write permission at first run. |
| 6 | No user interaction required after setup | Once initially configured (scale identified, permissions granted), the app should run unattended: detect scale → connect → capture stable weight → write to Health Connect → disconnect. |
| 7 | Minimal status visibility | A very basic screen or notification showing last sync time/weight and any error state is sufficient — no need for a polished UI. |

## 5. Non-Functional / Platform Constraints

- **Platform:** Android only. Target a reasonably recent minimum API level compatible with Health Connect (Health Connect is built into the OS on Android 14+; on Android 9–13 it requires the separate Health Connect app to be installed — note this dependency to the user in setup instructions).
- **Language/framework:** C#, using .NET for Android (or .NET MAUI configured for Android-only output — no need for iOS/Windows targets).
- **Distribution:** Not for the Play Store. Deliver as a signed release APK for manual installation ("sideloading"). Include brief instructions for enabling "install unknown apps" for the transfer method used (e.g. file transfer, cloud storage link).
- **Permissions:** Request Android Bluetooth permissions (`BLUETOOTH_SCAN`, `BLUETOOTH_CONNECT` on Android 12+) and Health Connect write permission for weight records. Location permission should not be required if scan filters avoid needing it, but confirm this on the target Android version.
- **Reliability:** The scan-wake mechanism should survive app process death and phone reboot (register the scan filter persistently, e.g. via a boot-completed receiver re-registering it if needed).
- **No cloud dependency:** All processing happens on-device; no server component.

## 6. Deliverables

1. Source code for the Android app (C#, .NET for Android/MAUI project).
2. A short written note confirming the corrected QN-Scale byte layout for this specific scale (per Section 3).
3. A signed release APK ready for sideloading.
4. Brief setup instructions: initial pairing/identification of the scale, granting permissions, and confirming Health Connect is installed and connected.

## 7. Open Questions for the Developer to Resolve During Build

- Exact byte offset and scale factor for the weight value on this specific Archonfit unit (see Section 3).
- Whether a stability/settled flag bit exists in the payload, and which bit it is.
- Whether the scale exposes a MAC address that's stable enough to filter on directly, or whether filtering by advertised name is more reliable.
- Minimum Android OS version to target, based on the phone this will run on.
