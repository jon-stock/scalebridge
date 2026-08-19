using System.Collections.Generic;
// Confirmed against a real build: the binding generator produces "Androidx" (lowercase "ndroidx"),
// not "AndroidX", for this Maven-resolved library - unlike the NuGet-distributed AndroidX
// bindings (e.g. AndroidX.Core.App used in Status/SyncNotifier.cs), which do use "AndroidX".
using Androidx.Health.Connect.Client;
using Androidx.Health.Connect.Client.Records;
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
/// </summary>
public static class HealthConnectWriter
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    public const string WriteWeightPermission = "android.permission.health.WRITE_WEIGHT";
    private const string PermissionControllerJavaClassName = "androidx.health.connect.client.PermissionController";
    private const string WeightRecordJavaClassName = "androidx.health.connect.client.records.WeightRecord";
    private const string MetadataJavaClassName = "androidx.health.connect.client.records.metadata.Metadata";
    private const string InstantJavaClassName = "java.time.Instant";
    private const string ZoneOffsetJavaClassName = "java.time.ZoneOffset";
    private const string MassJavaClassName = "androidx.health.connect.client.units.Mass";

    public static bool IsAvailable(Context context)
    {
        // HealthConnectClient.getSdkStatus(...) returns SDK_AVAILABLE (3) once Health Connect is
        // installed/available for this app, whether built into the OS (Android 14+) or provided
        // by the separate Health Connect app (Android 9-13) - Prompt.md Section 5.
        return HealthConnectClient.GetSdkStatus(context) == HealthConnectClient.SdkAvailable;
    }

    /// <summary>
    /// Builds the <c>ActivityResultContract</c> used to request Health Connect permissions, for
    /// use with AndroidX Activity's <c>RegisterForActivityResult</c> (see MainActivity.cs) - the
    /// correct way to request this permission, unlike a plain <c>RequestPermissions</c> call
    /// (which silently does nothing on many devices, since Health Connect permissions are not
    /// always wired into the standard OS permission dialog).
    ///
    /// Built via Java reflection against the real, confirmed
    /// <c>PermissionController.createRequestPermissionResultContract()</c> factory
    /// (androidx.health.connect.client.PermissionController.kt) rather than a direct C# static
    /// call: <c>PermissionController</c> is a Kotlin interface with a companion `@JvmStatic`
    /// factory, and - per the `Mass.Kilograms` naming collision found earlier in this file -
    /// Health Connect's Kotlin bindings have repeatedly surprised us with what the binding
    /// generator actually ends up calling things. Reflection sidesteps needing to know that at
    /// all: it only depends on the real, source-confirmed Java class/method name, plus the
    /// separately-referenced, long-established `AndroidX.Activity` binding for the return type.
    /// </summary>
    public static AndroidX.Activity.Result.Contract.ActivityResultContract CreatePermissionRequestContract()
    {
        using var controllerClass = Java.Lang.Class.ForName(PermissionControllerJavaClassName);
        // The real method has a @JvmOverloads default parameter (an optional provider package
        // name); GetMethod with zero parameter types resolves the generated no-arg overload,
        // avoiding any need to know/guess the default package name string.
        using var method = controllerClass.GetMethod("createRequestPermissionResultContract");
        var result = method.Invoke(null);
        return (AndroidX.Activity.Result.Contract.ActivityResultContract)result!;
    }

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
    /// This previously called <c>new WeightRecord(instant, null, weight, null)</c> directly, which
    /// compiled fine but crashed at runtime with "parameter specified as non-null is null: method
    /// androidx.health.connect.client.records.WeightRecord.&lt;init&gt;": the real Kotlin
    /// constructor's 4th parameter (<c>metadata: Metadata</c>) only has a default value
    /// (<c>Metadata.EMPTY</c>) for callers going through Kotlin itself - a direct JVM constructor
    /// call (which is what a bound C# `new WeightRecord(...)` compiles down to) has no such
    /// default and requires a real, non-null <c>Metadata</c> instance. (The 2nd parameter,
    /// <c>zoneOffset</c>, is genuinely nullable in the real API, so passing <see langword="null"/>
    /// for that one is correct and not the cause of this error.)
    ///
    /// Confirmed against the real AndroidX Kotlin source
    /// (androidx.health.connect.client.records.metadata.Metadata.kt) that <c>Metadata</c>'s
    /// constructor is <c>internal</c> - it can only be built via one of its companion's
    /// <c>@JvmStatic</c> factory functions, of which <c>manualEntry()</c> (an
    /// <c>@JvmOverloads</c> function whose zero-argument overload defaults its optional
    /// <c>device</c> parameter to <see langword="null"/>) is the correct one for a manually
    /// captured scale reading like this app's.
    ///
    /// Both the <c>Metadata.manualEntry()</c> call and the entire <c>WeightRecord</c> construction
    /// are done here via plain Java reflection, rather than as direct C# calls, deliberately
    /// following the same pattern as <see cref="CreateMassInKilograms"/> immediately below: this
    /// sidesteps needing to know/guess what the .NET binding generator actually calls the
    /// <c>Metadata</c> type (it lives in a Kotlin subpackage,
    /// <c>androidx.health.connect.client.records.metadata</c>, that this file has no existing
    /// `using` for, and this project has repeatedly hit binding-generator naming surprises - see
    /// docs/PROTOCOL_CONFIRMATION.md) and instead depends only on the real, source-confirmed JVM
    /// class/method names. The final result is cast back to the concrete, already-used
    /// <see cref="WeightRecord"/> type (not the untyped reflection result), so the rest of this
    /// method can use it exactly as before.
    /// </summary>
    private static WeightRecord CreateWeightRecord(Java.Time.Instant instant, Mass weight)
    {
        using var metadataClass = Java.Lang.Class.ForName(MetadataJavaClassName);
        // manualEntry() is @JvmOverloads with a single optional `device` parameter, so the JVM
        // also exposes a genuine zero-argument overload - no need to pass a null Device through.
        using var manualEntryMethod = metadataClass.GetMethod("manualEntry");
        using var metadata = manualEntryMethod.Invoke(null);

        using var weightRecordClass = Java.Lang.Class.ForName(WeightRecordJavaClassName);
        using var instantClass = Java.Lang.Class.ForName(InstantJavaClassName);
        using var zoneOffsetClass = Java.Lang.Class.ForName(ZoneOffsetJavaClassName);
        using var massClass = Java.Lang.Class.ForName(MassJavaClassName);
        using var constructor = weightRecordClass.GetConstructor(instantClass, zoneOffsetClass, massClass, metadataClass);
        var result = constructor.NewInstance(instant, null, weight, metadata);
        return (WeightRecord)result!;
    }

    /// <summary>
    /// Constructs a <see cref="Mass"/> via <c>Mass.kilograms(double)</c>, confirmed against the
    /// real androidx.health.connect.client.units.Mass.kt source to be a genuine
    /// <c>@JvmStatic</c> factory method on the companion object. It is invoked here via plain
    /// Java reflection rather than as a direct C# static method call: <c>Mass</c> also has an
    /// instance property getter <c>getKilograms()</c> (for reading an existing Mass back out in
    /// kilograms), and the two collided under whatever name the binding generator would
    /// otherwise have given the static factory - confirmed by `Mass.Kilograms(weightKg)` failing
    /// to compile with "non-invocable member" (i.e. only the property survived under that name,
    /// with the factory method bound under some other, unknown name). Reflection sidesteps that
    /// naming ambiguity entirely by calling the real, stable JVM method directly.
    /// </summary>
    private static Mass CreateMassInKilograms(double weightKg)
    {
        using var massClass = Java.Lang.Class.FromType(typeof(Mass));
        using var doubleType = Java.Lang.Double.Type!;
        using var method = massClass.GetMethod("kilograms", doubleType);
        using var boxedValue = new Java.Lang.Double(weightKg);
        var result = method.Invoke(null, boxedValue);
        return (Mass)result!;
    }
}
