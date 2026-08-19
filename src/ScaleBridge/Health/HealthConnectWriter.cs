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
    ///
    /// The class itself is looked up via the calling <paramref name="context"/>'s own
    /// `ClassLoader` (<c>Context.getClassLoader()</c> - the same, single, standard app
    /// classloader that loads every class in the APK, including Maven-resolved dependencies
    /// like this one), rather than <c>Class.ForName(name)</c> (whose caller-classloader
    /// resolution is meaningless when called via JNI from managed code, not a real Java/Kotlin
    /// call site - confirmed to actually fail this way at runtime; see the fuller explanation on
    /// <see cref="CreateWeightRecord"/>) or <c>Class.FromType(typeof(HealthConnectClient))</c>
    /// (tried next, and confirmed on a real device to throw
    /// <c>ClassNotFoundException: mono.internal.androidx.health.connect.client.HealthConnectClient</c>
    /// - <c>HealthConnectClient</c> is a Kotlin interface with a companion object, and its C#
    /// binding is apparently a synthetic/static-only helper type with no real, separately
    /// loadable Java class of its own for `FromType`'s <c>JNIEnv.FindClass(Type)</c> to resolve,
    /// unlike concrete Kotlin classes such as <c>Mass</c> - see docs/PROTOCOL_CONFIRMATION.md).
    /// </summary>
    public static AndroidX.Activity.Result.Contract.ActivityResultContract CreatePermissionRequestContract(Context context)
    {
        using var controllerClass = context.ClassLoader!.LoadClass(PermissionControllerJavaClassName)!;
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
            var record = CreateWeightRecord(context, instant, weight);

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
    /// The <c>Metadata.manualEntry()</c> call and the <c>WeightRecord</c> construction are both
    /// done here via plain Java reflection, rather than as direct C# calls, deliberately
    /// following the same pattern as <see cref="CreateMassInKilograms"/> immediately below: this
    /// sidesteps needing to know/guess what the .NET binding generator actually calls the
    /// <c>Metadata</c> type (it lives in a Kotlin subpackage,
    /// <c>androidx.health.connect.client.records.metadata</c>, that this file has no existing
    /// `using` for, and this project has repeatedly hit binding-generator naming surprises - see
    /// docs/PROTOCOL_CONFIRMATION.md) and instead depends only on the real, source-confirmed JVM
    /// class/method names. The final result is cast back to the concrete, already-used
    /// <see cref="WeightRecord"/> type (not the untyped reflection result), so the rest of this
    /// method can use it exactly as before.
    ///
    /// This has already gone through three failed fix attempts on a real device, all replaced
    /// here - see docs/PROTOCOL_CONFIRMATION.md for the full history:
    ///
    /// 1. Looking up every class here by name via <c>Java.Lang.Class.ForName(name)</c>. Built and
    ///    ran, but the "last sync error" on a real device was just the bare string
    ///    <c>androidx.health.connect.client.records.metadata.Metadata</c> - the signature of a
    ///    JVM <c>ClassNotFoundException</c> (whose message is only ever the class name). Root
    ///    cause: <c>Class.forName(String)</c>'s single-argument overload resolves against
    ///    whatever <c>ClassLoader</c> the *calling Java stack frame* belongs to, which is
    ///    meaningless for a call made via JNI from managed/Mono code rather than a real Java
    ///    caller, and can miss classes that only exist in the app's Maven-resolved dependency dex.
    /// 2. Resolving the classes that already have known, working C# bindings
    ///    (<c>WeightRecord</c>, <c>Instant</c>, <c>ZoneOffset</c>, <c>Mass</c>) via
    ///    <c>Java.Lang.Class.FromType(typeof(...))</c> instead, and loading <c>Metadata</c> (which
    ///    has no confirmed C# binding) through <c>weightRecordClass.ClassLoader</c>. This also
    ///    built and ran, but crashed <em>earlier</em> than the write path entirely: the identical
    ///    <c>FromType</c> approach, applied to <see cref="CreatePermissionRequestContract"/> for
    ///    <see cref="HealthConnectClient"/>, threw
    ///    <c>ClassNotFoundException: mono.internal.androidx.health.connect.client.HealthConnectClient</c>
    ///    from <c>MainActivity.OnCreate</c> - <c>HealthConnectClient</c> is a Kotlin interface
    ///    with a companion object, and apparently has no real, separately loadable Java class for
    ///    <c>FromType</c>'s <c>JNIEnv.FindClass(Type)</c> to resolve (unlike concrete Kotlin
    ///    classes like <c>Mass</c>, which is why <see cref="CreateMassInKilograms"/> was never
    ///    affected). That specific call was caught by `MainActivity`'s existing defensive
    ///    try/catch, which is also why "Health Connect available" showed as unavailable/disabled
    ///    afterwards - not a real availability check failure, a swallowed startup exception.
    /// 3. Switching to <c>Context.getClassLoader()</c> for every class lookup (fixing both of the
    ///    above), but still assuming <c>Metadata.manualEntry()</c> (zero-argument) existed as a
    ///    plain <c>public static</c> method directly on the outer <c>Metadata</c> class - the
    ///    <c>@JvmStatic</c> annotation on a companion function is *supposed* to bridge it there.
    ///    Built and ran, but threw <c>NoSuchMethodException:
    ///    androidx.health.connect.client.records.metadata.Metadata.manualEntry []</c> from
    ///    <c>Class.GetMethod("manualEntry")</c> - that specific zero-argument bridge evidently
    ///    isn't generated on the outer class for this function (whether because
    ///    <c>@JvmStatic</c>+<c>@JvmOverloads</c> together don't bridge every reduced-arity
    ///    overload, or some other Kotlin-compiler-version-specific detail), even though the
    ///    one-argument <c>manualEntry(Device)</c> form and/or the real implementation on
    ///    <c>Metadata$Companion</c> presumably still exist somewhere.
    ///
    /// Fixed by no longer guessing a single specific (declaring class, method name, arity)
    /// combination at all: <see cref="FindAndInvokeManualEntry"/> below searches the real,
    /// actually-reachable public methods at runtime - first every public method directly on
    /// <c>Metadata</c> (in case a compatible static bridge does exist), then every public
    /// instance method on the singleton <c>Metadata.Companion</c> object (which is *guaranteed*
    /// to have a real, callable implementation of every companion function, since that's what
    /// <c>@JvmStatic</c> bridges are bridging *to* - obtained via the actual companion field's
    /// value's own <see cref="Java.Lang.Object.Class"/>, not a guessed
    /// <c>"Metadata$Companion"</c> class name) - and calls whichever `manualEntry` overload it
    /// finds first, with zero or one (null) arguments as appropriate. This is more code than
    /// hard-coding one exact call, but after three wrong guesses about this library's exact
    /// Kotlin-to-JVM bridging shape in a row, discovering it at runtime is more reliable than a
    /// fourth guess. `CreatePermissionRequestContract`'s equivalent zero-argument
    /// `createRequestPermissionResultContract()` lookup is left as a direct `GetMethod` call
    /// since that one is confirmed working (it's an ordinary interface companion function, not
    /// one combining `@JvmStatic` with `@JvmOverloads` on a reduced-arity overload like this one).
    /// </summary>
    private static WeightRecord CreateWeightRecord(Context context, Java.Time.Instant instant, Mass weight)
    {
        using var classLoader = context.ClassLoader!;
        using var weightRecordClass = classLoader.LoadClass(WeightRecordJavaClassName)!;
        using var instantClass = classLoader.LoadClass(InstantJavaClassName)!;
        using var zoneOffsetClass = classLoader.LoadClass(ZoneOffsetJavaClassName)!;
        using var massClass = classLoader.LoadClass(MassJavaClassName)!;
        using var metadataClass = classLoader.LoadClass(MetadataJavaClassName)!;

        using var metadata = FindAndInvokeManualEntry(metadataClass);

        using var constructor = weightRecordClass.GetConstructor(instantClass, zoneOffsetClass, massClass, metadataClass);
        var result = constructor.NewInstance(instant, null, weight, metadata);
        return (WeightRecord)result!;
    }

    /// <summary>
    /// Finds and calls a callable <c>manualEntry</c> overload for <paramref name="metadataClass"/>
    /// (<c>androidx.health.connect.client.records.metadata.Metadata</c>), without assuming in
    /// advance whether it's a zero- or one-argument overload, or whether it lives directly on the
    /// class (as a <c>@JvmStatic</c> bridge) or only on the real <c>Metadata.Companion</c>
    /// instance - see the longer explanation on <see cref="CreateWeightRecord"/> for why this
    /// discovers it at runtime instead of hard-coding another guess.
    /// </summary>
    private static Java.Lang.Object FindAndInvokeManualEntry(Java.Lang.Class metadataClass)
    {
        if (TryInvokeManualEntry(metadataClass, receiver: null, out var metadata))
            return metadata;

        using (var companionField = metadataClass.GetField("Companion"))
        using (var companion = companionField.Get(null))
        {
            if (companion is not null)
            {
                using var companionClass = companion.Class;
                if (TryInvokeManualEntry(companionClass, companion, out metadata))
                    return metadata;
            }
        }

        throw new InvalidOperationException(
            "Could not find a callable Metadata.manualEntry(...) overload via reflection " +
            "(checked public methods on both the Metadata class and its Companion instance).");
    }

    /// <summary>
    /// Looks for a public method named <c>manualEntry</c> taking zero or one parameters on
    /// <paramref name="declaringClass"/>, and invokes the first one found against
    /// <paramref name="receiver"/> (<see langword="null"/> for a static method, the real
    /// <c>Metadata.Companion</c> instance for an instance method on it), passing
    /// <see langword="null"/> for the one-parameter case (the real Kotlin function's only
    /// parameter, <c>device</c>, is nullable/optional).
    /// </summary>
    private static bool TryInvokeManualEntry(Java.Lang.Class declaringClass, Java.Lang.Object? receiver, out Java.Lang.Object metadata)
    {
        // GetMethods()/GetParameterTypes() return plain C# arrays of JNI-backed objects, not a
        // disposable container of their own - each element is disposed individually below.
        var methods = declaringClass.GetMethods()!;
        foreach (var method in methods)
        {
            using (method)
            {
                if (method.Name != "manualEntry")
                    continue;

                var parameterTypes = method.GetParameterTypes()!;
                try
                {
                    switch (parameterTypes.Length)
                    {
                        case 0:
                            metadata = method.Invoke(receiver)!;
                            return true;
                        case 1:
                            metadata = method.Invoke(receiver, (Java.Lang.Object?)null)!;
                            return true;
                    }
                }
                catch (Java.Lang.Exception)
                {
                    // This specific overload wasn't actually callable this way (e.g. a
                    // non-nullable parameter that rejected null) - keep looking.
                }
            }
        }

        metadata = null!;
        return false;
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
