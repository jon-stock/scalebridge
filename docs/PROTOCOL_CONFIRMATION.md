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
- Once that resolved, the build's Java dependency verification step (XAJDV) rejected
  `connect-client`'s transitive Java dependencies (Kotlin stdlib/coroutines, Guava, AndroidX
  Core/Annotation/Activity) because Microsoft already maintains NuGet bindings for all of them
  and expects those referenced explicitly rather than re-bound from Maven. Fixed by adding the
  eight `PackageReference`s XAJDV named.
- Pinning those eight to the exact minimum versions XAJDV first asked for then caused NU1605
  package-downgrade errors: `Xamarin.AndroidX.Core` (already referenced for the status
  notification) transitively requires higher floors of several of the same packages via its own
  dependency chain (e.g. `Xamarin.AndroidX.Lifecycle.Runtime.Android` -> a newer
  `Xamarin.KotlinX.Coroutines.Android`). Fixed by bumping all eight to their latest available
  NuGet version instead of the bare minimum, which satisfies every floor at once rather than
  chasing them one at a time. See the comment above the `PackageReference`s in
  `ScaleBridge.csproj` for the one that looks wrong but isn't
  (`Xamarin.Google.Guava.ListenableFuture` at version `9999.0.0`, a deliberate Guava placeholder
  version).
- That "latest of each" pass had two more mistakes: `Xamarin.AndroidX.Annotation` and
  `Xamarin.AndroidX.Activity`'s latest-version lookups got transposed (each was given the other's
  version, and Annotation's `1.13.0.1` doesn't exist for that package - `NU1102`), and bumping
  `Xamarin.AndroidX.Core.Core.Ktx` to its latest version introduced a new floor on plain
  `Xamarin.AndroidX.Core` (which is also referenced directly, for `NotificationCompat`) that its
  old pinned version no longer satisfied (  `NU1605` again). Fixed by correcting the transposed
  versions and bumping `Xamarin.AndroidX.Core` to match `Core.Ktx`.
- With all seven other transitive dependencies satisfied, XAJDV still rejected
  `com.google.guava:listenablefuture:1.0` even though it's pinned (correctly) to a real NuGet
  version. This one is a genuine limitation of XAJDV rather than a mistake: modern Guava (see
  `Xamarin.Google.Guava` in `ScaleBridge.csproj`) bundles the real `ListenableFuture` class
  internally and depends on a deliberately-empty placeholder release of the standalone artifact
  purely to avoid a duplicate-class conflict between the two. Referencing the *real* artifact
  instead of that placeholder would reintroduce the exact conflict Guava's placeholder trick
  exists to avoid, so the placeholder pin is correct and XAJDV simply can't see that Guava
  already satisfies the requirement. Fixed by adding `VerifyDependencies="false"` to the
  `connect-client` `AndroidMavenLibrary` item, after fixing the other seven dependencies for
  real - see the comment above that item in `ScaleBridge.csproj`.

With the dependency graph settled, the next CI run reached real C# compile errors - genuinely
useful ones, unlike the environment/versioning noise above:

- The binding generator produces the namespace `Androidx.Health.Connect.Client...` (lowercase
  `ndroidx`) for this Maven-resolved library, not `AndroidX.Health.Connect.Client...` as
  originally guessed. Fixed in `HealthConnectWriter.cs`. (Note this is unrelated to, and cased
  differently from, the NuGet-distributed AndroidX bindings such as `AndroidX.Core.App` used in
  `Status/SyncNotifier.cs`, which do use `AndroidX`.)
- `kotlin.coroutines.Continuation`'s generated C# interface member is a `Context` **property**,
  not a `GetContext()` method as originally guessed. Fixed in `KotlinContinuationBridge.cs`.
- A handful of classes in `connect-client` that this app never uses - exercise-route, error, and
  changes-event plumbing - failed to bind cleanly: they don't implement an inherited abstract
  member, a known class-parse limitation with certain Kotlin protobuf-backed classes and
  nullable-generic `ActivityResultContract` overrides, not something fixable from this project's
  own C# code. First fix attempt excluded those four specific classes via
  `Transforms/Metadata.xml`.
- That per-class exclusion turned out to be the wrong granularity: the next build uncovered many
  more failures in the same family, spanning the *entire* internal
  `androidx.health.platform.client` package (connect-client's IPC/protobuf transport layer used to
  talk to the Health Platform service - protobuf-lite's abstract generic `Builder` pattern and an
  AIDL-generated service-stub pattern both failed to bind), plus a second broken
  `ActivityResultContract` class (`HealthPermissionsRequestContract`) alongside the first
  (`ExerciseRouteRequestContract`). Fixed by excluding the whole `androidx.health.platform.client`
  package tree (via `starts-with()` on the package name) and the whole
  `androidx.health.connect.client.contracts` package, rather than continuing to chase individual
  classes - both are internal/unused as far as ScaleBridge is concerned, which only ever calls the
  public `HealthConnectClient`/`WeightRecord`/`Mass`/`InsertRecordsResponse` API surface.
- With those internal packages excluded, the next failures were all ten classes in
  `androidx.health.connect.client.units` (`Mass`, `Length`, `Energy`, `Power`, `Pressure`,
  `Percentage`, `Temperature`, `Velocity`, `Volume`, `BloodGlucose`) - each fails to implement
  `IComparable.CompareTo(Object)`. Unlike the previous failures, `Mass` in particular is a class
  this app actually needs (`Mass.Kilograms(...)` in `HealthConnectWriter.cs`), so it can't simply
  be excluded. Since every one of these generated binding classes is `partial`, fixed by
  completing the missing interface member in ordinary C# (`Health/UnitsComparableFixups.cs`)
  rather than via a metadata transform - ScaleBridge never actually calls `CompareTo` on any of
  them, so each stub only needs to satisfy the compiler.
- That first attempt implemented `System.IComparable` and compiled cleanly, but the identical
  error persisted unchanged on the next build - a sign the fix targeted the wrong interface
  entirely rather than being incomplete. The interface the compiler actually needs implemented is
  `Java.Lang.IComparable` (the bound `java.lang.Comparable` interface, whose `CompareTo` takes a
  `Java.Lang.Object`), a real, long-standing part of Mono.Android, not `System.IComparable` (the
  BCL interface, whose `CompareTo` takes a plain `object`) - the two are unrelated types that
  happen to share a short name. Fixed by reimplementing each stub in
  `Health/UnitsComparableFixups.cs` against `Java.Lang.IComparable` instead.

With the Health Connect binding fully resolved, the next build reached the last category of
errors: genuine mistakes in ScaleBridge's own application code (not binding-generator issues),
all straightforward once seen:

- `Record` (the sealed Kotlin interface every Health Connect record type implements) is bound as
  `IRecord`, not a `Record` class - fixed the `List<...>` in `HealthConnectWriter.cs` accordingly.
- `Mass.Kilograms(weightKg)` doesn't compile: it resolves to the *instance* property from
  Kotlin's `getKilograms()` getter (for reading an existing `Mass` back out in kilograms), not
  the static `kilograms(double)` factory - the real AndroidX Kotlin source (`Mass.kt`) confirms
  the factory genuinely exists as a `@JvmStatic` companion method, but it collided by name with
  that getter and the binding generator resolved the collision in the property's favour, leaving
  the factory bound under some other, unconfirmed name. Fixed by invoking the real JVM method
  directly via Java reflection (`CreateMassInKilograms` in `HealthConnectWriter.cs`), which
  sidesteps the naming ambiguity entirely rather than guessing what the factory ended up being
  called.
- `BluetoothGattDescriptor.EnableIndicationValue`/`EnableNotificationValue` are bound as
  `IList<byte>`, not `byte[]` - fixed with `.ToArray()` in `QnScaleSession.cs`.
- A `FailAndStop(...)` call in `ScaleConnectionService.cs` was missing its required `isError`
  argument.
- `Intent.GetParcelableArrayListExtra(...)` returns a raw, untyped `IList`, not
  `IList<IParcelable>` - fixed the null-coalescing/iteration in `ScaleScanReceiver.cs`.
- `ScanMode` was ambiguous between `Android.Bluetooth.ScanMode` (classic Bluetooth
  discoverability) and `Android.Bluetooth.LE.ScanMode` (BLE scan power/latency) - fully qualified
  in `ScaleScanRegistrar.cs`.
- An `as Java.Lang.Throwable` cast in `KotlinContinuationBridge.cs`'s failure-extraction path
  didn't compile; replaced with a plain `ToString()` on the raw `Java.Lang.Object`, since that
  path only needs a diagnostic string, not the real exception type.

That left one remaining error: `WeightRecord` doesn't convert to `IRecord` at all ("cannot
convert from WeightRecord to IRecord"), even though `Record` (confirmed against the real
AndroidX source) is a trivial one-member interface (`metadata: Metadata`) that `WeightRecord`
already implements via its constructor. This is the same category of gap as the earlier
`IComparable`/`ActivityResultContract` issues - a missing interface declaration somewhere in the
generated binding hierarchy - rather than an incomplete one. Fixed the same way: declaring the
interface directly on `WeightRecord` in `Health/RecordInterfaceFixups.cs`. Unlike the
`IComparable` fixups, no member stub was needed here, since `WeightRecord` already has a
compatible `Metadata` member for C# to satisfy the interface with implicitly.

**As of this point, the app builds and signs cleanly in CI** (`.github/workflows/build-apk.yml`),
including the Health Connect write path. Practically, confidence levels are now:

- **High confidence, and compiling in CI:** `QnFrameParser` (pure C#, no Android types, ported
  line-by-line from a real, current, actively-maintained reference implementation) and the
  general BLE scan/GATT/service structure (standard, long-established Android APIs).
- **Now compiling, but functionally unverified:** the Health Connect write path in
  `src/ScaleBridge/Health/`. Every binding-generator gap and naming mismatch it hit has been
  fixed and explained above, and the code now builds - but none of it has actually run against a
  real Health Connect instance yet (no emulator/device was available in the environment that
  fixed these compile errors). The `Mass.Kilograms` reflection workaround in particular
  (`CreateMassInKilograms` in `HealthConnectWriter.cs`) should be treated as unverified until a
  real sync has been observed to actually appear in Health Connect - if it throws at runtime,
  that function is the first place to look.
- **Still unverified:** the actual GATT behaviour against the real Archonfit unit - no substitute
  for the logcat check described above has been done yet. Do this before trusting the very first
  automatic sync.

## Post-first-install feedback (device testing)

Once installed on a real phone, the "Grant Health Connect write permission" button turned out to
do nothing visible. The root cause: a plain `RequestPermissions(["android.permission.health.
WRITE_WEIGHT"], ...)` call - which is what the app originally used, and which the code comments
even flagged as a possible risk - does not reliably show the system permission dialog for Health
Connect permissions on many devices/Android versions. The correct mechanism is Health Connect's
own `PermissionController.createRequestPermissionResultContract()` `ActivityResultContract`,
which opens Health Connect's own permission screen.

Implementing that switched `MainActivity` from the plain framework `Activity` to AndroidX's
`ComponentActivity` (needed for `RegisterForActivityResult`), and built the contract itself via
Java reflection against the real, source-confirmed
`androidx.health.connect.client.PermissionController.createRequestPermissionResultContract()`
method - deliberately avoiding yet another guess at what the Health Connect Kotlin binding calls
things, given how many times that assumption has been wrong so far in this document. This is
genuinely new, untested binding surface (an `ActivityResultContract<Set<String>, Set<String>>`
return type, `AndroidX.Activity`'s own generic-erased `RegisterForActivityResult`/
`ActivityResultLauncher` API), so - consistent with everything else Health Connect has touched in
this project - treat it as unverified until it's actually been exercised on a device.

Two other changes made at the same time, both much lower-risk (standard, long-established Android
APIs, no Kotlin/Health-Connect binding involved): the debug BLE scan results are now a tappable
list that fills in the MAC address field directly, and the whole screen was restyled using
Material Components (`Xamarin.Google.Android.Material`) instead of plain unstyled widgets.

That styling pass did hit one real (Android-tooling, not Health-Connect-related) issue:
`ScaleBridge.SectionTitle` and `ScaleBridge.BodyText` in `styles.xml` had no explicit `parent`
attribute. Android's build tooling auto-derives an implicit parent from everything before the
last dot in a style's name when `parent` is omitted (a real behaviour, not just a naming
convention) - so it looked for a style literally named `ScaleBridge` and failed to link
resources. Fixed by adding an explicit empty `parent=""` to both.

## Crash on first launch, and defensive hardening

Once that build succeeded and was installed on the real phone, the app crashed immediately on
opening - before this session could see any logcat output (no `adb` access on the phone used for
testing). Rather than guess a second time at exactly which of the two brand-new, never-run
features caused it (the reflection-built Health Connect permission contract, or the newly-added
Material Components widgets, which have their own well-known runtime check that the Activity's
theme is actually a Material theme), this was addressed two ways at once:

1. **A global crash handler** (`ScaleBridgeApplication.cs`, installed via `Application.OnCreate`,
   which runs before any Activity's `OnCreate`) persists the full exception text via
   `Status/CrashLog.cs`, and `MainActivity` shows it in a "Last crash" card (with selectable text,
   so it can be copied) whenever one is present. This means any future crash - this one included,
   if it recurs - is readable directly from the app, without needing `adb`.
2. **The riskiest new call** (`RegisterForActivityResult(HealthConnectWriter.
   CreatePermissionRequestContract(), ...)` in `MainActivity.OnCreate`) is now wrapped in a
   try/catch. If it throws, the rest of the screen still loads normally and the Health Connect
   permission button shows a clear message instead of taking the whole app down with it.

This does not identify a single root cause with certainty (that requires seeing the actual
crash text, which - per point 1 - should now be visible in the app itself after the next
install), but it directly addresses the two most likely candidates and ensures the app is no
longer a black box if something else was actually responsible.

That build was installed and still crashed immediately - before the app visibly opened at all,
i.e. before `MainActivity`'s "Last crash" card could ever have had a chance to render, since that
card depends on the app surviving long enough to show its main screen even once. This pointed at
a gap in the previous fix rather than disproving it: persisting the crash to SharedPreferences is
useless if the *next* launch attempt fails at the same early point every time, which a
deterministic startup bug would do.

Fixed by decoupling crash *visibility* from `MainActivity` entirely:

- `CrashActivity.cs` is a dedicated, minimal crash screen built entirely in plain code (no layout
  resource, no Material Components, no app theme/styles) - specifically so it doesn't depend on
  anything that could plausibly be part of whatever just broke.
- `ScaleBridgeApplication`'s crash handler now launches `CrashActivity` directly the moment any
  unhandled exception is caught, instead of only persisting the text for `MainActivity` to show
  later.
- `CrashLog.Record` additionally writes the exception to a plain text file
  (`Android/data/uk.co.accessuk.scalebridge/files/crash_log.txt`), recoverable via USB/MTP or
  `adb pull` even if no on-screen crash UI manages to appear at all.

If a crash still shows nothing whatsoever after this - not even the dedicated crash screen, and
no file at that path - that would point to something happening at a lower level than managed
.NET/Java code can intercept (e.g. a native/JNI-level failure during process or library
initialization), which genuinely cannot be diagnosed further without `adb logcat` access to the
device at the moment of the crash.

That crash screen worked exactly as intended and immediately surfaced the real cause, which
turned out to be a plain, mundane Android layout mistake with nothing to do with any of the
Health Connect/Material-theme/reflection theories above:
`Java.Lang.UnsupportedOperationException: Binary XML file line #1 ...: You must supply a
layout_width attribute.` Root cause: the `ScaleBridge.SectionTitle` and `ScaleBridge.BodyText`
styles (used, unstyled with their own explicit `layout_width`/`layout_height`, on most of the
section-heading `TextView`s in `activity_main.xml`) never defined those two properties. Every
Android `View` requires both, supplied either directly on the tag or via its style, with no
default - this isn't caught at build time by AAPT2, only at runtime when the layout is actually
inflated, which is why it got this far before failing. Fixed by adding
`android:layout_width`/`android:layout_height` to both styles.

## Icon and notification icon

Replaced the placeholder flat icon with a proper Android adaptive icon (`mipmap-anydpi-v26/
ic_launcher.xml`): a dark green background (`#0B3D2E`) and a simple white bathroom-scale glyph
(`drawable/ic_launcher_foreground.xml`) kept within the adaptive icon's safe zone so it isn't
clipped by circular/squircle/rounded-square launcher masks. Added a separate, single-silhouette
notification icon (`drawable/ic_notification.xml`): status bar icons render only their alpha
channel (tinted by the system), so reusing the full-colour launcher icon there would have looked
like a solid blob rather than a recognisable shape.

## Post-second-install usability feedback

Three plain UI bugs, all standard Android issues unrelated to Health Connect/Kotlin binding
risk:

- The "not granted" Health Connect permission message was shown as a Toast, which disappears
  on its own after a few seconds and was too long to reliably read in time. Replaced with an
  AlertDialog (stays up until dismissed) in MainActivity.OnHealthConnectPermissionResult.
- The debug-scan device ListView couldn't be scrolled once more devices were found than fit in
  its fixed-height container. Root cause: it's nested inside the screen's outer ScrollView,
  which is a well-known Android trap - the outer ScrollView intercepts the drag gesture before
  the list itself ever sees it. Fixed with the standard workaround: a Touch handler on the list
  that tells its parent not to intercept touches starting on the list, while still letting the
  list process them normally.
- Tapping a device appeared to do nothing to the MAC address field, even though the code was
  filling it in correctly - the field lives in a card further down the screen, below the device
  list, so nothing drew the user's attention to scroll down and see it. Fixed by calling
  RequestFocus() on the field after populating it: a descendant of a ScrollView requesting
  focus makes the ScrollView automatically scroll to bring it into view.

## First real scale test: connection never happened at all

The scale's own Bluetooth indicator never lit up when testing against the real device - a
previously-working app (used for comparison) does light it up, meaning the scale itself is fine
and the fault is in this app's connection-triggering path, not the GATT/protocol layer.

Root cause, found by inspecting the manifest rather than guessing: `Ble/ScaleScanReceiver` was
declared with `android:exported="true"` but **no `<intent-filter>`** for the action it's meant to
receive (`uk.co.accessuk.scalebridge.ACTION_SCALE_FOUND`). The BLE scan-result `PendingIntent` is
delivered as an implicit broadcast (action + package, no explicit component); without a matching
`<intent-filter>`, Android's broadcast resolution has no way to match it to this receiver at all,
so it's silently dropped before the receiver's `OnReceive` ever runs - the entire wake-on-scan
mechanism was non-functional. This has been present since the very first version of the manifest;
this was simply the first time the real end-to-end BLE flow was ever exercised. Fixed by adding
the missing `<intent-filter>`.

This does not yet confirm the GATT/protocol layer itself works correctly against the real
Archonfit unit - only that a connection attempt should now actually be triggered. The logcat
verification step in this document's "What still needs confirming on the real device" section
is still the next thing to do.

## White-on-white input text

Reported after the second real install: text typed/filled into the MAC/name fields was invisible
(white text on a white field). Root cause: `AppTheme` used
`Theme.MaterialComponents.DayNight.NoActionBar`, which automatically switches to dark-mode
default colours (light text, meant for a dark background) when the phone's system dark mode is
on - but every background colour in this app (`colors.xml`) is a single hardcoded light value
with no night-mode variant, so on a phone in dark mode the text defaulted to light-on-light.
Fixed by using the explicitly-Light theme variant instead of DayNight (this is a small,
single-purpose utility screen - not worth properly supporting both light and dark mode for), plus
explicit `android:textColor`/`textColorHint` directly on the input fields as a second safeguard.

## Health Connect refusing to grant permission directly ("go via Health Connect")

Tapping "Grant Health Connect write access" opened Health Connect, but instead of the normal
allow/deny screen it showed a message saying this had to be managed from Health Connect's own
settings instead. This is documented, expected Health Connect behaviour, not a bug in the
reflection-based permission code from earlier: Health Connect requires the requesting app to
declare a "permissions rationale" intent-filter (and be visible to it under Android 11+ package
visibility rules) before it will let the app request permissions directly - confirmed against
Google's own Health Connect sample app manifest
(https://github.com/android/health-samples/blob/main/health-connect/HealthConnectSample/app/src/main/AndroidManifest.xml),
which was missing both:

- A `<queries><package android:name="com.google.android.apps.healthdata" /></queries>` entry, so
  this app can even see the Health Connect package.
- Two intent-filters on `MainActivity` (`androidx.health.ACTION_SHOW_PERMISSIONS_RATIONALE`, and
  `android.intent.action.VIEW_PERMISSION_USAGE` with category
  `android.intent.category.HEALTH_PERMISSIONS`) that Health Connect uses to link back to the
  app's own rationale/privacy screen. For this small, single-user personal app, no dedicated
  rationale screen was built - `MainActivity`'s existing default screen is a reasonable-enough
  destination for these.

Both are now declared; this should let the normal Health Connect grant screen appear instead of
the redirect message.

## Crash loop: "Unable to instantiate receiver ... ClassNotFoundException"

After the connection fix above (adding the missing `<intent-filter>`), `ScaleScanReceiver` finally
started actually being triggered by the OS for the first time - and immediately crashed on every
single trigger, fast enough (each retriggered by the scale re-advertising) to look like a crash
loop that couldn't be read. The full exception, eventually captured via the crash screen, was
unambiguous:

```
Java.Lang.RuntimeException: Unable to instantiate receiver uk.co.accessuk.scalebridge.Ble.ScaleScanReceiver
 ---> Java.Lang.ClassNotFoundException: Didn't find class "uk.co.accessuk.scalebridge.Ble.ScaleScanReceiver" ...
```

Root cause: **.NET for Android does not give a managed class a Java name that literally matches
its C# namespace by default.** It generates a hashed name instead (confirmed from an earlier
crash log: `MainActivity`'s real Java class name is `crc6427e3e38310646c4d.MainActivity`, not
`uk.co.accessuk.scalebridge.MainActivity`) - that only appears correct in the manifest for
`MainActivity` because it's registered via the `[Activity(...)]` C# attribute, which makes the
build tool write the correct (hashed) name into the manifest automatically.

`ScaleScanReceiver`, `BootCompletedReceiver`, and `ScaleConnectionService` were all, by contrast,
hand-declared directly in `Properties/AndroidManifest.xml` (a deliberate choice for auditability -
see the comment at the top of that file), using relative names like `.Ble.ScaleScanReceiver`.
Android resolves that to the literal Java class `uk.co.accessuk.scalebridge.Ble.ScaleScanReceiver`
- which never existed, since none of the three classes had anything forcing their generated Java
name to match. **This means all three hand-declared components have been broken this way since
the very first version of the manifest** - `ScaleScanReceiver` is simply the first of the three
to have ever actually been triggered by the OS (`BootCompletedReceiver` needs an actual reboot;
`ScaleConnectionService` is only started by `ScaleScanReceiver`, which itself only started
working two fixes ago).

Fixed with `[Register("...")]` on all three classes - the standard, documented mechanism for
forcing a class's generated Java name to match a specific hand-written value, letting a manifest
entry like `.Ble.ScaleScanReceiver` correctly resolve to the intended C# type.

## Crash writing to Health Connect: "parameter specified as non-null is null" in WeightRecord

The Health Connect write path (flagged above as "compiling, but functionally unverified") was
exercised for the first time and immediately threw:

```
parameter specified as non-null is null: method androidx.health.connect.client.records.WeightRecord.<init>
```

Root cause: `HealthConnectWriter.WriteWeightAsync` built the record with
`new WeightRecord(instant, null, weight, null)`. The real Kotlin constructor's 4th parameter
(`metadata: Metadata`) has a default value (`Metadata.EMPTY`), but that default only applies to
callers going through the Kotlin compiler - a direct JVM constructor call (which is what the bound
C# `new WeightRecord(...)` compiles down to) has no such default and requires a real, non-null
`Metadata` instance, which nothing in this codebase had ever constructed. (The 2nd parameter,
`zoneOffset`, is also `null` here, but that one is genuinely nullable in the real API, so it isn't
implicated.)

Confirmed against the real AndroidX Kotlin source
(`androidx.health.connect.client.records.metadata.Metadata.kt`) that `Metadata`'s constructor is
`internal` - a caller can only obtain one via the companion's `@JvmStatic` factory functions, of
which `manualEntry()` (defaulting its optional `device` parameter to `null` via `@JvmOverloads`)
is the correct choice for a manually captured scale reading.

Fixed in `CreateWeightRecord` (`HealthConnectWriter.cs`): both the `Metadata.manualEntry()` call
and the `WeightRecord` construction itself are now done via plain Java reflection, rather than a
direct C# constructor call, for the same reason as `CreateMassInKilograms` a few lines below it -
`Metadata` lives in a Kotlin subpackage (`androidx.health.connect.client.records.metadata`) this
file has no existing `using` for, and this project has repeatedly hit binding-generator naming
surprises (see `Mass.Kilograms` above), so reflection avoids needing to guess what the .NET
binding actually calls that type. The reflection result is cast back to the concrete, already-used
`WeightRecord` type, so the rest of `WriteWeightAsync` is unchanged.

This fix has not yet been re-verified against a real Health Connect instance on a device; per this
document's ongoing pattern, if a *different* null/type error appears next, the newly-added
`CreateWeightRecord` is the first place to look.

## Second Health Connect crash: bare "androidx.health.connect.client.records.metadata.Metadata"

The fix above was installed and tested on the real device. The null-metadata crash was gone, but
the write still failed - the notification read "Captured 100.0 kg but Health Connect write failed:
androidx.health.connect.client.reco..." (truncated by Android's notification text limit) and the
"Last sync error" card (not length-limited) showed the full message verbatim: just
`androidx.health.connect.client.records.metadata.Metadata`, with no other words at all.

That bare "just the class name, nothing else" shape is the signature of a JVM
`ClassNotFoundException` (its `getMessage()` really is only ever the class name it failed to
find) - not a new variant of the null-metadata bug. Root cause: `CreateWeightRecord`'s first fix
attempt resolved *every* class it needed - including `WeightRecord`, `Instant`, `ZoneOffset`, and
`Mass`, all four of which already have known, working, directly-usable C# bindings - via
`Java.Lang.Class.ForName(name)`, the same single-argument overload already used (and, as far as
this document knew at the time, apparently working) in `CreatePermissionRequestContract`.
`Class.forName(String)`'s single-argument overload resolves against whatever `ClassLoader` the
*calling Java stack frame* belongs to. That resolution has no reliable meaning here: these calls
are invoked via JNI from managed/Mono code, not from an actual Java/Kotlin call site, so there is
no normal caller stack frame for it to inspect, and it can end up resolving against a classloader
(e.g. the platform/bootstrap one) that was never given access to this app's Maven-resolved
`connect-client` dependency dex in the first place - explaining why a real, present class
(`Metadata` genuinely exists and ships in the APK) can still throw `ClassNotFoundException`.

Fixed in `CreateWeightRecord` two ways at once:

- The four classes that already have real, known C# bindings (`WeightRecord`, `Instant`,
  `ZoneOffset`, `Mass`) are now resolved via `Java.Lang.Class.FromType(typeof(...))` instead of
  `ForName(name)` - the same reliable mechanism `CreateMassInKilograms` already used for `Mass`,
  which sidesteps the whole caller-classloader question by asking Mono's Java.Interop layer for
  the class of an already-bound managed peer type directly, rather than searching for it by name.
- `Metadata` has no known C# binding to call `FromType` on (that's the entire reason it's resolved
  by name at all - see above), but it doesn't need `Class.forName`'s ambiguous caller-classloader
  behaviour either: it's loaded via `weightRecordClass.ClassLoader!.LoadClass(name)` instead - the
  exact `ClassLoader` that successfully loaded `WeightRecord` a moment earlier via the reliable
  `FromType` path. Since `WeightRecord` and `Metadata` come from the same library/dex, that
  classloader is guaranteed to already have access to `Metadata` too.

`CreatePermissionRequestContract`'s pre-existing `Class.ForName(PermissionControllerJavaClassName)`
call has the identical latent bug - it happened not to have thrown yet only because the Health
Connect permission grant flow hadn't been re-tested since the "go via Health Connect" settings
fix earlier in this document, not because it's actually safe. Fixed the same way, pre-emptively,
using `HealthConnectClient`'s `ClassLoader` (a real, already-used, definitely-working class) to
load `PermissionController` by name instead of `Class.ForName(name)` directly.

Separately, since diagnosing this relied entirely on `ex.Message` alone being visible (which,
for exactly this class of JVM exception, is nearly useless on its own), the Health Connect
write-failure catch block in `ScaleConnectionService.OnWeightCaptured` now also calls
`CrashLog.Record(this, ex)` - the same full-detail (`ex.ToString()`, including the underlying Java
stack trace/"Caused by" chain for JNI exceptions) persistence mechanism `ScaleBridgeApplication`'s
global handler and `MainActivity`'s permission-contract try/catch already used, surfaced via the
existing "Last crash" card - without this being a fatal, unhandled crash. The short "Last sync
error" text and notification now also include the exception's type name
(e.g. `ClassNotFoundException: ...`) alongside its message, and point at "Last crash" for the
full detail. This is a pure diagnostics improvement, independent of whether the classloader fix
above turns out to be complete - if a third Health Connect error appears, "Last crash" should now
contain enough information (real exception type, real message, real stack trace) to diagnose it
without another round trip like this one.

## Third Health Connect crash: `ClassNotFoundException: mono.internal...HealthConnectClient`, and why "not available on this build" appeared

The classloader fix above was installed and tested. It crashed differently, and this time the full
stack trace (thanks to the diagnostics improvement in the previous section, though this one
actually crashed early enough to be a normal unhandled exception with a full logcat-style trace)
pointed straight at the cause:

```
Java.Lang.ClassNotFoundException: mono.internal.androidx.health.connect.client.HealthConnectClient
  at Java.Lang.Class.FromType(Type)
  at ScaleBridge.Health.HealthConnectWriter.CreatePermissionRequestContract()
  at ScaleBridge.MainActivity.OnCreate(Bundle savedInstanceState)
```

This also explains a second symptom reported at the same time: tapping "Grant Health Connect
write access" said Health Connect "wasn't available on this build". `CreatePermissionRequestContract()`
is called from `MainActivity.OnCreate` inside a deliberate try/catch (added earlier in this
document, in "Crash on first launch, and defensive hardening") that sets
`_healthPermissionLauncher = null` on failure - so this `ClassNotFoundException` was silently
caught there, and the resulting null launcher is exactly what makes `RequestHealthConnectPermission`
show "Health Connect permission request isn't available on this build". Not a real
availability/installation problem - a swallowed startup exception from the fix in the previous
section.

Root cause: the previous fix used `Java.Lang.Class.FromType(typeof(HealthConnectClient))` to get a
`ClassLoader` known to have access to this library, on the theory that `HealthConnectClient` "is
already a real, compile-time-bound, working C# type, so `FromType` on it must be safe" - it had
been used successfully (as ordinary typed static calls, `HealthConnectClient.GetOrCreate(context)`/
`GetSdkStatus(context)`) since the very start of this file. That theory was wrong specifically for
`FromType`: `HealthConnectClient` is a Kotlin *interface* with a companion object (unlike `Mass` or
`WeightRecord`, both genuine concrete Kotlin classes), and its C# binding for the companion's
static factory methods is apparently a synthetic, static-only helper type with no real,
separately-loadable Java class backing it at all - ordinary typed static method calls to it work
fine (they're wired directly to the real companion object's methods internally), but
`Class.FromType(Type)` - which calls `JNIEnv.FindClass(Type)` to compute and look up an actual
loadable Java class name for that C# type - has nothing real to find, and apparently computes some
internal-only placeholder name (`mono.internal....`) instead of ever finding the true class.
`PermissionController` (used in the exact same call, one level down) is the same kind of Kotlin
interface-with-companion, so it likely has the same problem and simply hadn't been tried via
`FromType` directly.

Fixed by removing `Class.FromType`/`Class.ForName` from this whole area entirely, in favour of one
single, unambiguous mechanism: `Context.getClassLoader()`. This is a plain, standard Android API
(not a Xamarin/Mono binding-generator construct at all) that returns the one real `ClassLoader`
actually used to load every class in the running app's APK, including Maven-resolved dependencies
like `connect-client` - there's no separate/isolated classloader per library in a normal .NET for
Android app. Both `CreatePermissionRequestContract` (now takes a `Context` parameter, threaded
through from `MainActivity.OnCreate`'s `this`) and `CreateWeightRecord` (already had a `Context`
available, from `WriteWeightAsync`) now load every class they need
(`PermissionController`/`WeightRecord`/`Instant`/`ZoneOffset`/`Mass`/`Metadata`) via
`context.ClassLoader!.LoadClass(name)`, rather than mixing `FromType` in for the ones that "should"
already work. The one exception is `Mass` inside `CreateMassInKilograms`, deliberately left
exactly as it was (`Class.FromType(typeof(Mass))`) since - unlike everything else touched across
these three fix attempts - it's the one part of this whole area actually confirmed to work
end-to-end on a real device, and changing working code here without a real bug to justify it would
just be introducing risk for no reason.

This is now the third fix attempt for this write path, each one only discoverable by actually
running on the real device - if a fourth error appears, it should at least now come with a full
stack trace (per the previous section's diagnostics work) pointing at exactly which
`LoadClass`/`GetMethod`/`GetConstructor`/`NewInstance` call failed and why.

## Crash: `startForegroundService() not allowed due to mAllowStartForeground false`

A real-device crash, unrelated to Health Connect - this one is `ScaleScanReceiver.OnReceive`
crashing the whole process with:

```
Java.Lang.IllegalStateException: startForegroundService() not allowed due to
mAllowStartForeground false: service uk.co.accessuk.scalebridge/.Ble.ScaleConnectionService
 ---> android.app.ForegroundServiceStartNotAllowedException: ...
```

This is a genuine, well-documented Android OS policy restriction (Android 12+, "foreground
service launch restrictions" - this app's `targetSdkVersion` is 34), not a binding/naming bug like
the three Health Connect issues above, and not something a manifest permission or
`foregroundServiceType` declaration can override. `ScaleScanReceiver` is woken by an *implicit
broadcast* delivered via the mutable `PendingIntent` registered in `ScaleScanRegistrar` through
`BluetoothLeScanner.startScan(filters, settings, pendingIntent)`. That kind of broadcast is not one
of the OS's specially-exempted types (unlike, say, `BOOT_COMPLETED`), so it does not automatically
grant the temporary "this component may start a foreground service" exemption - if the app has had
no recent foreground/visible activity when the scale's advertisement is seen (precisely the
"reacts with no user interaction" scenario this receiver exists for), the OS can and does refuse
the `StartForegroundService(...)` call outright, and - since nothing caught it - the resulting
exception took the whole process down.

Fixed immediately, defensively, in `ScaleScanReceiver.OnReceive`: the `StartForegroundService` call
is now wrapped in a try/catch. On failure it no longer crashes - it logs the full exception via
`CrashLog.Record` (surfaced in "Last crash"), records a short reason via `StatusStore.RecordError`
("Last sync error"), and posts a plain notification via `SyncNotifier.PostError` telling the user
to open the app to sync manually this time. Posting a notification is not itself subject to this
restriction (only *starting a foreground service* is gated), so this fallback is reliable. There is
deliberately no retry loop: this is a deterministic app-state gate, not a transient failure, so
retrying immediately would just fail identically.

This is a safety net, not a real fix for the underlying reliability goal (Prompt.md Section 4,
"no user interaction required after setup") - it just turns "app silently crashes" into "app
degrades to a manual-open notification", which can still happen fairly often on a phone that's
been idle for a while. The actual Google-documented, purpose-built solution for "wake a
background-restricted app to start a foreground service when a companion BLE device comes into
range" is `android.companion.CompanionDeviceManager`'s background device-presence observation API
(`startObservingDevicePresence`/the older device-presence broadcast, API 26+/33+ depending on the
exact method), combined with the special
`android.permission.REQUEST_COMPANION_START_FOREGROUND_SERVICES_FROM_BACKGROUND` permission it
lets an associated app request - CDM-associated companion apps are specifically exempted from this
restriction, because this exact use case (fitness trackers, watches, scales) is what CDM was built
for. That's a real architecture change (one-time CDM association during setup, replacing or
supplementing the current `BluetoothLeScanner.startScan` + `PendingIntent` + `ScaleScanReceiver`
wake path) rather than a one-line fix, and hasn't been implemented yet - this section's fix is the
stop-the-crashing safety net while that decision is made.
