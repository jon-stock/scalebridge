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

**This one-time setup is already done** as of the build that introduced this fix: the
`ANDROID_KEYSTORE_BASE64` repository secret is set, and `build-apk.yml` uses it automatically -
every build from here on is signed with the same persisted key, so installing a new build over an
existing install works exactly like any other app update, with **one exception**: the very first
build signed with this new persisted key is *itself* signed with a different key than whatever
throwaway keystore signed your previously-installed copy, so that one specific update still needs
one last manual uninstall of the old copy first. Every build after that installs in place normally.

Before this was done, that workflow signed every build with a **fresh throwaway keystore generated
on each run**, purely so it worked out of the box - every new build had to be installed over an
*uninstalled* previous copy, since Android silently refuses to install an update signed by a
different key, which is easy to not notice and looks exactly like "I installed the new build but
the fix didn't work" when it's actually still running the old one. That's what the section below
describes setting up, kept here in case this ever needs to be redone (e.g. the secret is deleted,
or a new fork/repo needs its own keystore).

### One-time setup: a persisted signing keystore

So every future build can be installed as a normal in-place update instead of a throwaway keystore
per run:

1. In this repo's **Actions** tab, manually run the **"Generate persistent signing keystore (run
   once)"** workflow (`.github/workflows/generate-keystore.yml`) - it only needs running once,
   ever.
2. Once it finishes, download its `scalebridge-keystore-base64` artifact and open the
   `scalebridge.keystore.base64.txt` file inside it.
3. In this repo's **Settings -> Secrets and variables -> Actions**, add a **New repository
   secret** named exactly `ANDROID_KEYSTORE_BASE64`, and paste that file's entire contents as the
   value.
4. From then on, `build-apk.yml` automatically detects and uses that secret instead of generating
   a throwaway keystore - every build after this point is signed with the same key, so installing
   a new build over an existing install works exactly like any other app update (no uninstall
   needed). If you ever see the workflow log warn `ANDROID_KEYSTORE_BASE64 repo secret is not
   set`, this step wasn't completed (or the secret name doesn't match exactly).

The keystore/key passwords and alias this generates are deliberately plain, fixed values (see the
workflow file) - only the keystore *file itself* needs to be kept as a secret, since it can't be
regenerated if lost, unlike a password you chose yourself.

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

A crash immediately shows a dedicated, minimal crash screen with the full exception text
(long-press to select/copy it, or use its Share button to send the text to yourself). This screen
is launched directly by a global crash handler the moment anything goes wrong, independently of
whatever just crashed - including a crash during `MainActivity`'s own startup, before it can show
anything itself (its own "Last crash" card, shown when you next open the app normally, only helps
for crashes *after* the main screen has successfully rendered at least once).

The same details are also written to a plain text file, in case even that crash screen can't be
shown for some reason:

```
Android/data/uk.co.accessuk.scalebridge/files/crash_log.txt
```

This needs no special permissions to write (it's the app's own app-specific storage) but Android
11+ often hides `Android/data` from on-device file manager apps directly - the most reliable way
to reach it is to plug the phone into a PC over USB and browse to that path (exposed via MTP), or
use a file manager app that has "All files access" granted.

If you do have `adb` available, `adb logcat -s AndroidRuntime:E DEBUG:E` around the time of the
crash is the equivalent view, and `adb pull` can fetch the same crash log file directly:

```
adb pull /storage/emulated/0/Android/data/uk.co.accessuk.scalebridge/files/crash_log.txt
```

## Known limitation: reboot re-arming

`Boot.BootCompletedReceiver` re-registers the scan filter after a reboot, but only takes effect
the next time Android actually delivers `BOOT_COMPLETED` (i.e. after the phone restarts). If a
sync doesn't happen after a reboot, opening the app once (which also re-registers the scan) is a
quick manual workaround.
