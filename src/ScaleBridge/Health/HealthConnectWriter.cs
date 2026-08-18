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
            var record = new WeightRecord(instant, null, weight, null);

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
