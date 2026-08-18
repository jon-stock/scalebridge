# Setup instructions

## What you're installing

A single-purpose, sideloaded Android app (`uk.co.accessuk.scalebridge`) that watches for one
specific Archonfit BLE scale and writes each stable weight reading to Health Connect, unattended.
Not on the Play Store, not for anyone else's device. See `Prompt.md` for the full brief and
`docs/PROTOCOL_CONFIRMATION.md` for the protocol write-up.

## Building via CI (recommended)

The machine this project was generated on has a Group Policy that blocks the Android workload
installer's `dotnet.exe` subprocess, so it cannot build this app locally. `.github/workflows/
build-apk.yml` builds it instead on a GitHub-hosted runner, unaffected by that policy: push to
`main` (or trigger it manually from the repo's **Actions** tab), then download the signed
`ScaleBridge-apk` artifact from the finished run. See the repo's Actions tab for build status.

Note: that workflow currently signs every build with a **fresh throwaway keystore generated on
each run**, purely so it works out of the box. That means every new build must be installed over
an uninstalled previous copy (Android refuses to install an update signed by a different key).
Switch to one persisted keystore stored as a repo secret before relying on this for real,
in-place updates.

## Prerequisites (if building locally instead)

1. .NET SDK 10 (or the matching SDK for whatever `TargetFramework` ends up in
   `src/ScaleBridge/ScaleBridge.csproj`).
2. The Android workload: `dotnet workload install android`. This provisions the Android SDK/NDK
   and OpenJDK for you - you do not need Android Studio, though it's a fine way to get the same
   tooling and a device log viewer.
3. A phone with USB debugging enabled, connected via `adb`, or an APK you sideload manually.

## First build

```
cd src/ScaleBridge
dotnet build
```

The most likely source of compile errors on the very first build is the Health Connect binding
surface (`src/ScaleBridge/Health/HealthConnectWriter.cs` and `KotlinContinuationBridge.cs`) -
see the comments in those files and `docs/PROTOCOL_CONFIRMATION.md`. If member names don't match:

- Open `obj/Debug/<tfm>/android/bindings/AndroidX.Health.Connect.Client.dll` in ILSpy/dotPeek (or
  browse `obj/**/generated/**` for the generated binding source) to see the real generated
  namespaces/types the `AndroidMavenLibrary` reference produced from
  `androidx.health.connect:connect-client`.
- The Java package -> expected C# namespace mapping is listed in the comment block at the top of
  `HealthConnectWriter.cs`.
- If `client.InsertRecords(records, bridge)` doesn't match any overload, the suspend-fun's
  generated Java signature is visible from the same generated binding sources; adjust the call
  and `KotlinContinuationBridge<T>`'s `IContinuation` implementation to match.

Everything else in the project (BLE scanning, GATT, notifications, boot receiver, status
notification, shared-preferences status store) uses long-stable, ordinary Android APIs and should
build without surprises.

## Installing on the phone

```
dotnet build -t:Run -f net10.0-android    # deploys + launches on a connected/emulated device
```

or, for a real sideloaded release build:

```
dotnet publish -f net10.0-android -c Release -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=<path-to-your.keystore> \
  -p:AndroidSigningKeyAlias=<alias> \
  -p:AndroidSigningKeyPass=<key password> \
  -p:AndroidSigningStorePass=<store password>
```

Generate a keystore first if you don't have one (any name/alias/passwords - keep them, you'll
need the *same* keystore for every future update of this app, since Android refuses to install an
update signed by a different key over an existing install):

```
keytool -genkeypair -v -keystore scalebridge.keystore -alias scalebridge -keyalg RSA -keysize 2048 -validity 10000
```

`dotnet publish` produces a signed, ready-to-install `.apk` under
`bin/Release/net10.0-android/publish/`. Transfer it to the phone (USB, cloud storage link, email to
yourself - whatever's convenient) and open it from Files/Downloads to install. The phone will ask
to allow "install unknown apps" for whichever app you used to open the file (Files, Chrome,
Gmail, etc.) the first time - allow it just for that one app if prompted.

## On-phone setup (one-off)

1. Open ScaleBridge. Tap **"Grant Bluetooth / notification permissions"** and allow everything
   requested.
2. Confirm Health Connect is installed:
   - **Android 14+:** it's built in - nothing to install.
   - **Android 9-13:** install "Health Connect by Android" from the Play Store first.
   Then tap **"Grant Health Connect write permission"** in ScaleBridge and allow it.
3. Step on the scale so it powers on and starts advertising, then tap **"Scan for nearby BLE
   devices (15s)"**. Look for the entry that's clearly the scale (name containing something like
   "QN-Scale"/"Renpho-Scale", or just the one new device that appeared right as you stepped on).
   Note its MAC address.
4. Paste that MAC address into the address field and tap **"Save and start watching for this
   scale"**. (If the scale doesn't expose a usable/stable MAC, use the advertised name field
   instead - see `docs/PROTOCOL_CONFIRMATION.md` for background, and Prompt.md's open questions.)
5. Done. From now on: step on the scale -> the phone connects automatically in the background ->
   the weight is written to Health Connect -> a notification confirms it (or reports an error).
   The main screen also shows the last sync time/weight/error if you open the app again.

## Verifying the protocol on your specific unit

Before trusting the very first automatic sync, do the check described in
`docs/PROTOCOL_CONFIRMATION.md` ("What still needs confirming on the real device") - it's a 5
minute logcat check (`adb logcat -s ScaleBridge.Qn`), not a rebuild.

## If the app crashes

Open the app again - a "Last crash" card appears at the top of the screen if one was recorded,
with the full exception text (long-press to select/copy it) and a "Clear" button. This works
without `adb`, which is useful given this app is normally sideloaded onto a single personal phone.
If you do have `adb` available, `adb logcat -s AndroidRuntime:E DEBUG:E` around the time of the
crash is the equivalent view.

## Known limitation: reboot re-arming

`Boot.BootCompletedReceiver` re-registers the scan filter after a reboot, but only takes effect
the next time Android actually delivers `BOOT_COMPLETED` (i.e. after the phone restarts). If a
sync doesn't happen after a reboot, opening the app once (which also re-registers the scan) is a
quick manual workaround.
