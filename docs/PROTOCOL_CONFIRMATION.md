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
