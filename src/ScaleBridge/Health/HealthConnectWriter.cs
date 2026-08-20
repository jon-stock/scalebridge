using System.Collections.Generic;
// Confirmed against a real build: the binding generator produces "Androidx" (lowercase "ndroidx"),
// not "AndroidX", for this Maven-resolved library - unlike the NuGet-distributed AndroidX
// bindings (e.g. AndroidX.Core.App used in Status/SyncNotifier.cs), which do use "AndroidX".
using Androidx.Health.Connect.Client;
using Androidx.Health.Connect.Client.Records;
using Androidx.Health.Connect.Client.Records.Metadata;
using Androidx.Health.Connect.Client.Response;
using Androidx.Health.Connect.Client.Units;
using Android.Content;

namespace ScaleBridge.Health;

/// <summary>
/// Writes a single captured weight reading to Android Health Connect with no user interaction
/// (Prompt.md Section 4, requirement 5) using androidx.health.connect:connect-client, pulled in
/// via the AndroidMavenLibrary reference in ScaleBridge.csproj.
///
/// This file, together with <see cref="KotlinContinuationBridge{TResult}"/>, is the single
/// highest-risk part of this project: the connect-client library is Kotlin, its
/// <c>insertRecords</c> call is a `suspend fun`, and the exact shape the .NET binding generator
/// produces for that (the generated method signature, the `Continuation` interface, and the
/// `Mass`/`WeightRecord` factory methods) could not be confirmed by an actual `dotnet build` in
/// this environment - see docs/PROTOCOL_CONFIRMATION.md for why, and what to check/fix first.
///
/// Every type used directly here (<see cref="Metadata"/>, <see cref="DataOrigin"/>,
/// <see cref="WeightRecord"/>, <see cref="Mass"/>, <see cref="IPermissionController"/>) is used
/// exactly as it appears in the *actual, generated* C# binding source, confirmed by downloading
/// and reading the `health-connect-binding-dump` artifact produced by `build-apk.yml` (a real CI
/// run's `obj/` output) - not guessed from Kotlin source. This replaced five earlier attempts at
/// this file that all relied on guessing what the .NET binding generator would call something,
/// each only disprovable by a real device crash - see docs/PROTOCOL_CONFIRMATION.md for the full
/// history and why the binding dump was worth adding. If this file ever needs to change again,
/// download that artifact from the latest `build-apk.yml` run and read the real generated source
/// before guessing at another API shape.
/// </summary>
public static class HealthConnectWriter
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    public const string WriteWeightPermission = "android.permission.health.WRITE_WEIGHT";

    public static bool IsAvailable(Context context)
    {
        // HealthConnectClient.getSdkStatus(...) returns SDK_AVAILABLE (3) once Health Connect is
        // installed/available for this app, whether built into the OS (Android 14+) or provided
        // by the separate Health Connect app (Android 9-13) - Prompt.md Section 5.
        //
        // NOTE: this only confirms Health Connect itself is installed/available - it says nothing
        // about whether *this app* currently holds WRITE_WEIGHT permission. Use
        // HasWritePermissionAsync for that; conflating the two previously meant the app had no way
        // to detect a mid-session permission revocation (e.g. an OEM battery manager silently
        // pulling the grant back) short of an actual write failing.
        return HealthConnectClient.GetSdkStatus(context) == HealthConnectClient.SdkAvailable;
    }

    /// <summary>
    /// Checks Health Connect's *actual current* grant for <see cref="WriteWeightPermission"/>,
    /// via <c>PermissionController.getGrantedPermissions()</c> - not the one-off, session-scoped
    /// result of the last in-app permission request (which is all MainActivity previously showed,
    /// and which goes stale the instant the app process restarts or the OS silently revokes the
    /// grant in the background). This is what actually answers "is Health Connect still connected
    /// right now", which is the real, live status the main screen needs.
    ///
    /// Confirmed from the real, pinned `connect-client:1.1.0-alpha07` sources
    /// (`PermissionController.kt`): `getGrantedPermissions()` is a suspend fun instance member of
    /// `PermissionController`, reached via `HealthConnectClient.permissionController` (a plain
    /// `val` property) - bridged the same way as `InsertRecords` in
    /// <see cref="WriteWeightAsync"/>, via <see cref="KotlinContinuationBridge{TResult}"/>. Unlike
    /// every call already exercised on a real device in this file, this specific call has not yet
    /// been - callers should treat a thrown exception here defensively (see MainActivity's usage),
    /// same as every other not-yet-device-verified Health Connect call in this project (see
    /// docs/PROTOCOL_CONFIRMATION.md).
    /// </summary>
    public static async Task<bool> HasWritePermissionAsync(Context context)
    {
        if (!IsAvailable(context))
            return false;

        return await Task.Run(() =>
        {
            var client = HealthConnectClient.GetOrCreate(context);
            var bridge = new KotlinContinuationBridge<Java.Lang.Object>();
            client.PermissionController!.GetGrantedPermissions(bridge);
            var result = bridge.AwaitResult(CallTimeout);
            return result is Java.Util.ISet set && set.Contains(new Java.Lang.String(WriteWeightPermission));
        });
    }

    /// <summary>
    /// Builds the <c>ActivityResultContract</c> used to request Health Connect permissions, for
    /// use with AndroidX Activity's <c>RegisterForActivityResult</c> (see MainActivity.cs) - the
    /// correct way to request this permission, unlike a plain <c>RequestPermissions</c> call
    /// (which silently does nothing on many devices, since Health Connect permissions are not
    /// always wired into the standard OS permission dialog).
    ///
    /// <see cref="IPermissionController.CreateRequestPermissionResultContract()"/> is a real,
    /// confirmed-from-the-actual-generated-binding-source (see this class's own doc comment)
    /// direct static call - no reflection needed. Earlier attempts used
    /// <c>Class.ForName</c>/<c>Class.FromType</c> reflection here instead, guessing that
    /// <c>PermissionController</c> (a Kotlin interface with a companion factory) had no usable
    /// direct C# binding; that guess was wrong - it does, it's just declared as a C# 11 static
    /// interface member on <see cref="IPermissionController"/>, not on a separate class. The
    /// *obsolete* <c>Androidx.Health.Connect.Client.PermissionController</c> class some IDEs may
    /// suggest instead is a trap: its Java registration name is a synthetic
    /// <c>mono/internal/androidx/health/connect/client/PermissionController</c> with no real,
    /// separately loadable backing class - confirmed to throw <c>ClassNotFoundException</c> on a
    /// real device when something analogous was tried for <c>HealthConnectClient</c> (see
    /// docs/PROTOCOL_CONFIRMATION.md).
    /// </summary>
    public static AndroidX.Activity.Result.Contract.ActivityResultContract CreatePermissionRequestContract() =>
        IPermissionController.CreateRequestPermissionResultContract();

    /// <summary>The Set&lt;String&gt; of permissions this app needs - just WRITE_WEIGHT.</summary>
    public static Java.Util.HashSet BuildRequiredPermissionSet()
    {
        var set = new Java.Util.HashSet();
        set.Add(new Java.Lang.String(WriteWeightPermission));
        return set;
    }

    /// <summary>
    /// Given the Set&lt;String&gt; of granted permissions the ActivityResultContract callback
    /// receives (typed as a plain Java object at the C# call site, since the contract's real
    /// type parameter is erased), checks whether it includes the one permission this app needs.
    /// </summary>
    public static bool GrantedSetIncludesWritePermission(Java.Lang.Object? activityResult) =>
        activityResult is Java.Util.ISet set && set.Contains(new Java.Lang.String(WriteWeightPermission));

    public static async Task WriteWeightAsync(Context context, double weightKg, DateTimeOffset whenUtc)
    {
        if (!IsAvailable(context))
            throw new InvalidOperationException("Health Connect is not available/installed on this device.");

        await Task.Run(() =>
        {
            var client = HealthConnectClient.GetOrCreate(context);

            var instant = Java.Time.Instant.OfEpochMilli(whenUtc.ToUnixTimeMilliseconds());
            var weight = CreateMassInKilograms(weightKg);
            var record = CreateWeightRecord(instant, weight);

            // Kotlin's `Record` is a sealed interface; the .NET binding exposes it as `IRecord`,
            // not a `Record` class/type - confirmed against a real build.
            var records = new List<IRecord> { record };

            var bridge = new KotlinContinuationBridge<InsertRecordsResponse>();
            // insertRecords(List<? extends Record>, Continuation<? super InsertRecordsResponse>)
            // - the last, compiler-synthesised Continuation parameter is what makes this call
            // synchronous-from-our-side despite being an `async`/suspend API on the Kotlin side.
            client.InsertRecords(records, bridge);
            bridge.AwaitResult(CallTimeout);
        });
    }

    /// <summary>
    /// Builds the <see cref="WeightRecord"/> to insert, given an already-constructed
    /// <c>Instant</c> and <see cref="Mass"/>.
    ///
    /// A direct <c>new WeightRecord(instant, null, weight, null)</c> call here was the very first
    /// version of this method: it compiled fine (the real constructor's <c>metadata</c> parameter
    /// is non-nullable Kotlin-side, but a plain C# reference-type parameter still accepts a literal
    /// <see langword="null"/> without a cast, and `Nullable` being enabled only warns, it doesn't
    /// block the build) but crashed at runtime with "parameter specified as non-null is null:
    /// method androidx.health.connect.client.records.WeightRecord.&lt;init&gt;". Getting from
    /// there to a real, correct, non-null <see cref="Metadata"/> took five more attempts on a real
    /// device, all guessing at Java reflection call shapes against Kotlin source that (it turned
    /// out) didn't even match the actual pinned library version - see
    /// docs/PROTOCOL_CONFIRMATION.md for the full history.
    ///
    /// Fixed for good by downloading and reading the actual generated C# binding source (see this
    /// class's own doc comment) instead of guessing at another reflection shape: `Metadata`,
    /// `DataOrigin`, and `WeightRecord` all have perfectly ordinary, directly-callable public C#
    /// constructors - there was never any need for reflection here at all. Values passed to
    /// <see cref="Metadata"/> match its own Kotlin-side defaults exactly (empty `id`, empty
    /// `DataOrigin`, `Instant.Epoch`, no client record id/version, no device), except
    /// `recordingMethod`, set to the real <see cref="Metadata.RecordingMethodManualEntry"/>
    /// constant instead of the default `RecordingMethodUnknown`, since that's an accurate,
    /// genuinely better description of how this app's readings are actually captured.
    /// </summary>
    private static WeightRecord CreateWeightRecord(Java.Time.Instant instant, Mass weight)
    {
        var metadata = new Metadata(
            id: string.Empty,
            dataOrigin: new DataOrigin(string.Empty),
            lastModifiedTime: Java.Time.Instant.Epoch!,
            clientRecordId: null,
            clientRecordVersion: 0L,
            device: null,
            recordingMethod: Metadata.RecordingMethodManualEntry);

        return new WeightRecord(instant, null, weight, metadata);
    }

    /// <summary>
    /// Constructs a <see cref="Mass"/> via the real, confirmed-from-the-actual-generated-binding
    /// (see this class's own doc comment) direct static <see cref="Mass.InvokeKilograms(double)"/>
    /// method - the C# name the binding generator gave the real Kotlin
    /// <c>Mass.Companion.kilograms(Double)</c> factory function, to avoid colliding with the
    /// separate, already-present instance property <see cref="Mass.Kilograms"/> (for reading an
    /// existing <see cref="Mass"/> back out in kilograms) that would otherwise have claimed the
    /// more obvious `Kilograms` name. This was originally called via Java reflection instead,
    /// before `InvokeKilograms` specifically was known to exist - that reflection call is exactly
    /// as correct, but this direct call is simpler now that it's confirmed to exist.
    /// </summary>
    private static Mass CreateMassInKilograms(double weightKg) => Mass.InvokeKilograms(weightKg);
}
