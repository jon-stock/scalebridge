# Protocol confirmation (Prompt.md Section 3 deliverable)

## Summary

Prompt.md Section 3 asks for the byte layout to be confirmed against a real BLE capture from the
specific Archonfit unit before writing the parsing logic. **That capture could not be done here**:
this is a text-based coding session with no access to the physical scale, no Android phone
attached, and (see "Build environment limitations" below) no Android SDK/JDK could even be
installed in this sandbox. Nothing in this document is a substitute for the HCI-snoop-log /
openScale-file-log capture Section 3 describes - that step still needs to happen on the actual
phone with the actual scale before the first real sync is trusted.

What *could* be done, and what materially changes the plan in Prompt.md Section 2/3:

## The referenced file has been superseded

Prompt.md points at `BluetoothQNScale.java`. That file no longer exists in openScale's
repository - the whole Bluetooth layer was rewritten in Kotlin. Its direct replacement is
[`QNHandler.kt`](https://github.com/oliexdev/openScale/blob/master/android_app/app/src/main/java/com/health/openscale/core/bluetooth/scales/QNHandler.kt),
which is materially different from (and newer than) what Prompt.md describes, and its code
comments and change history describe fixing almost exactly the symptoms Prompt.md Section 2
worried about for the Archonfit's zero-weight bug:

- **"FIXED: Protocol type race condition"** - the handler used to send the unit/time
  configuration to the scale immediately on connect. `QNHandler.kt` now deliberately waits for a
  `0x12` "scale info" frame (which carries the correct weight scale factor) before sending any
  configuration, and calls this out in its own comments as the previous root cause of bad
  readings.
- **Weight scale factor is not fixed at /100.** Byte `[10]` of the `0x12` frame says whether raw
  weight values need dividing by 100 or by 10. Using the wrong divisor produces exactly the kind
  of wrong/zero-looking weight Prompt.md Section 2 describes.
- **An alternate "ES-30M" byte layout exists** for some QN sub-variants, with the stable flag and
  weight at different offsets than the original layout. The handler auto-detects which layout is
  in use from the shape of the incoming `0x10` frame.
- A stability flag *does* exist (this answers Prompt.md's open question about it): in the
  original layout it's `data[5] == 1`; in the ES-30M layout it's `data[4] == 1 or 2`. Both layouts
  are implemented in `ScaleBridge.Ble.QnFrameParser`.
- Some sub-variants additionally require a two-frame `0xA0` acknowledgement handshake after a
  `0x21` frame, and a stored-measurement (`0x23`) fallback path with retry, before they will ever
  emit a live weight - both are implemented.

`src/ScaleBridge/Ble/QnFrameParser.cs` and `src/ScaleBridge/Ble/QnScaleSession.cs` are a
line-by-line C# port of `QNHandler.kt`'s framing, checksums, and handshake sequencing (opcodes
`0x10`/`0x12`/`0x14`/`0x20`/`0x21`/`0x22`/`0x23`/`0xA0`/`0xA1`/`0xA3`), not a re-derivation from
the older Java file. This is a strictly stronger starting point than Prompt.md's Section 2/3
describes, but it is still a generic "QN family" implementation, not one confirmed against this
specific Archonfit unit.

## What still needs confirming on the real device

Do this exactly as Prompt.md Section 3 describes, before trusting the first real sync:

1. Install the app (see `docs/SETUP.md`), enable verbose logcat (`adb logcat -s ScaleBridge.Qn`),
   and step on the scale. `QnScaleSession` logs the raw hex of every notification it receives,
   the opcode it dispatched to, which of the two byte layouts it picked, and the resulting
   weight/stable flag - this *is* the BLE capture Section 3 asks for, just taken via logcat
   instead of the HCI snoop log.
2. Confirm the `0x12` frame's byte `[10]` value (0 or 1) matches the divisor the logs say was
   applied, and that the resulting kg value is plausible.
3. Confirm which byte layout logcat reports (`Original` vs `Es30M`) and that the stable flag
   toggles only once you've actually stood still on the scale, not immediately on power-on.
4. If nothing arrives after `0x13`/`0x02` are sent, or the scale disconnects without ever sending
   `0x10`/`0x23`, capture an HCI snoop log as Prompt.md Section 3 describes and diff it against
   `QnScaleSession.HandleVendorPacket` - the Archonfit may need a handshake tweak beyond what
   `QNHandler.kt` already covers (it explicitly documents itself as covering several, but not
   guaranteed all, QN/Yolanda-chipset clones).

## Build environment limitations

The machine this project was authored on has no JDK/Android SDK, and its Group Policy blocks the
`android` .NET workload installer's `dotnet.exe` subprocess outright ("This program is blocked by
group policy"), so `dotnet build`/`dotnet publish` could never be run there directly. It also has
no physical Android phone or Archonfit scale attached.

The build instead runs on a GitHub Actions hosted runner (`.github/workflows/build-apk.yml`),
which isn't subject to that policy - see `docs/SETUP.md`. Its first few real build attempts
caught (and this project has since fixed) environment/versioning issues that had nothing to do
with the protocol logic:

- The project originally targeted `net9.0-android`, which the workload reports as out of support.
  Moved to `net10.0-android` (matching the workflow's `actions/setup-dotnet` version).
- `TargetPlatformVersion` was pinned to `34`, then `35.0` after the first fix - but the set of
  platform packs the Android workload actually ships moved twice within the same day (34 dropped,
  then 35.0 dropped too, leaving only 36.0/36.1). Rather than chase that moving target, the
  property is now omitted entirely so the SDK picks whichever platform pack is actually
  installed. This only affects the compile-time API surface, not the app's real minimum
  supported OS version (`SupportedOSPlatformVersion`, still `26`).
- The `AndroidMavenLibrary` reference for `androidx.health.connect:connect-client` 404'd:
  `AndroidMavenLibrary` defaults to resolving from Maven Central, but AndroidX artifacts
  (including this one) are published to Google's Maven repository and are not mirrored to
  Central. Fixed by adding `Repository="Google"` to that item in `ScaleBridge.csproj`.

Practically, confidence levels are now:

- **High confidence, and now compiling in CI:** `QnFrameParser` (pure C#, no Android types,
  ported line-by-line from a real, current, actively-maintained reference implementation) and the
  general BLE scan/GATT/service structure (standard, long-established Android APIs).
- **Still needs verification on the next CI run:** the Health Connect write path in
  `src/ScaleBridge/Health/`. `androidx.health.connect:connect-client` is a Kotlin library whose
  `insertRecords(...)` call is a `suspend fun`; calling it from C#/Java interop requires bridging
  a `kotlin.coroutines.Continuation`, and the exact C# names the .NET binding generator produces
  for that (and for the `Mass`/`WeightRecord`/`InsertRecordsResponse` types) had not been checked
  against a real build as of the first CI run. See the comments at the top of
  `HealthConnectWriter.cs` and `KotlinContinuationBridge.cs` for exactly what to check and fix if
  a future build reports errors there.
- **Still unverified:** the actual GATT behaviour against the real Archonfit unit - no substitute
  for the logcat check described above has been done yet.
