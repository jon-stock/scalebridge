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
    private const string DataOriginJavaClassName = "androidx.health.connect.client.records.metadata.DataOrigin";
    private const string DeviceJavaClassName = "androidx.health.connect.client.records.metadata.Device";
    private const string InstantJavaClassName = "java.time.Instant";
    private const string ZoneOffsetJavaClassName = "java.time.ZoneOffset";
    private const string MassJavaClassName = "androidx.health.connect.client.units.Mass";
    private const string StringJavaClassName = "java.lang.String";

    // androidx.health.connect.client.HealthConnectClient.DEFAULT_PROVIDER_PACKAGE_NAME (internal,
    // but a stable literal - confirmed against the real source for the exact connect-client
    // version this project pins, see CreatePermissionRequestContract).
    private const string DefaultProviderPackageName = "com.google.android.apps.healthdata";

    // androidx.health.connect.client.records.metadata.Metadata.RECORDING_METHOD_MANUAL_ENTRY -
    // confirmed against the real source for the exact connect-client version this project pins,
    // see CreateMetadata.
    private const int RecordingMethodManualEntry = 3;

    /// <summary>
    /// Every <c>ClassLoader.loadClass(name)</c> call in this file is wrapped in this instead of a
    /// bare <c>!</c> null-forgiving operator: per the documented `ClassLoader` contract, it should
    /// be genuinely impossible for `LoadClass` to return <see langword="null"/> (it must either
    /// return a real class or throw <c>ClassNotFoundException</c>) - but a real device produced
    /// exactly that impossible outcome once (see docs/PROTOCOL_CONFIRMATION.md: a
    /// <c>NoSuchMethodException: parameter type is null</c> several calls later, from
    /// <c>Class.GetConstructor</c>, with no way to tell which of four candidate classes was
    /// actually the null one). `!` only silences the compiler's static nullable-reference-type
    /// warning - it does nothing at runtime to prevent or explain an actual null reference. This
    /// throws immediately, naming the exact class that failed, so the *next* occurrence (whatever
    /// its ultimate root cause) is instantly diagnosable from "Last crash" alone instead of
    /// requiring another guess.
    /// </summary>
    private static Java.Lang.Class RequireClass(Java.Lang.Class? loaded, string javaClassName) =>
        loaded ?? throw new InvalidOperationException(
            $"ClassLoader.loadClass(\"{javaClassName}\") returned null instead of throwing or " +
            "returning a real class - see HealthConnectWriter.RequireClass.");

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
    ///
    /// The real function is
    /// <c>@JvmStatic @JvmOverloads fun createRequestPermissionResultContract(providerPackageName:
    /// String = DEFAULT_PROVIDER_PACKAGE_NAME)</c> - confirmed against the real
    /// <c>connect-client:1.1.0-alpha07</c> source (the exact version
    /// <c>ScaleBridge.csproj</c> pins - fetched via its Maven `-sources.jar`, since the AndroidX
    /// source browsable at HEAD on androidx-main turned out to be a materially different, newer
    /// API surface than what this app actually compiles/runs against - see
    /// <see cref="CreateMetadata"/> for where that distinction mattered even more). This calls
    /// the real one-argument overload directly with that exact default value, rather than
    /// `GetMethod` with zero parameter types for the `@JvmOverloads`-generated convenience
    /// overload: the identical `@JvmStatic`+`@JvmOverloads` combination on
    /// <c>Metadata.manualEntry</c> was confirmed on a real device to *not* bridge its
    /// zero-argument form onto the outer type, so this avoids relying on that same assumption
    /// holding here too.
    /// </summary>
    public static AndroidX.Activity.Result.Contract.ActivityResultContract CreatePermissionRequestContract(Context context)
    {
        using var classLoader = context.ClassLoader!;
        using var stringClass = RequireClass(classLoader.LoadClass(StringJavaClassName), StringJavaClassName);
        using var controllerClass = RequireClass(classLoader.LoadClass(PermissionControllerJavaClassName), PermissionControllerJavaClassName);
        using var method = controllerClass.GetMethod("createRequestPermissionResultContract", stringClass)
            ?? throw new InvalidOperationException($"{PermissionControllerJavaClassName}.createRequestPermissionResultContract(String) was not found via reflection.");
        using var providerPackageName = new Java.Lang.String(DefaultProviderPackageName);
        var result = method.Invoke(null, providerPackageName);
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
    /// androidx.health.connect.client.records.WeightRecord.&lt;init&gt;": the real 4th
    /// constructor parameter, <c>metadata: Metadata</c>, has no default from a direct JVM
    /// constructor call (which is what a bound C# `new WeightRecord(...)` compiles down to),
    /// only from Kotlin call sites. (The 2nd parameter, <c>zoneOffset</c>, is genuinely nullable
    /// in the real API, so passing <see langword="null"/> for that one is, and always was,
    /// correct.)
    ///
    /// Getting from there to a real, non-null <see cref="CreateMetadata"/> took four attempts on
    /// a real device - see docs/PROTOCOL_CONFIRMATION.md for the full history of the first three
    /// (a `Class.ForName` caller-classloader bug, a `Class.FromType` bug specific to Kotlin
    /// interfaces-with-companions, and a `Metadata.manualEntry()` zero-argument overload that
    /// doesn't actually exist as a direct static bridge). The root cause of that fourth attempt,
    /// and the reason this doc comment doesn't just describe a fifth reflection variant, is more
    /// fundamental: every one of those attempts (and this method's original design generally) was
    /// based on <c>Metadata.kt</c>/<c>WeightRecord.kt</c> as they exist today on the
    /// `androidx-main` development branch, browsed directly on
    /// <c>android.googlesource.com</c> - but `ScaleBridge.csproj` actually pins
    /// <c>androidx.health.connect:connect-client</c> to a specific, much older released version,
    /// <c>1.1.0-alpha07</c>. Downloading that exact version's real `-sources.jar` from Google's
    /// Maven repository (<c>https://maven.google.com/androidx/health/connect/connect-client/
    /// 1.1.0-alpha07/connect-client-1.1.0-alpha07-sources.jar</c>) and extracting the real
    /// `Metadata.kt` it ships showed a materially different, much simpler API: no `manualEntry`
    /// factory function exists at all in this version, no `EMPTY` constant is public, and
    /// `Metadata`'s constructor - unlike the `internal` one on `androidx-main` - is a plain
    /// <b>public</b> Kotlin constructor with every parameter defaulted:
    /// <c>Metadata(id: String = "", dataOrigin: DataOrigin = DataOrigin(""), lastModifiedTime:
    /// Instant = Instant.EPOCH, clientRecordId: String? = null, clientRecordVersion: Long = 0,
    /// device: Device? = null, recordingMethod: Int = RECORDING_METHOD_UNKNOWN)</c>. This
    /// constructor has no <c>@JvmOverloads</c>, so (like `WeightRecord`'s own constructor, also
    /// re-confirmed against this exact version's real source) the single real JVM constructor
    /// requires every parameter supplied explicitly - no bitmask-based "skip these, use defaults"
    /// mechanism is needed or usable from plain reflection - which <see cref="CreateMetadata"/>
    /// does directly, with no further guessing.
    ///
    /// Lesson for next time (recorded here since this is the highest-risk file in the project):
    /// when a Kotlin/Java class's *real, currently-compiled-against* API surface actually matters
    /// (as opposed to just its class/package name), fetch the sources jar for the exact pinned
    /// version from Maven - `androidx-main`/HEAD source is not a reliable stand-in for an older
    /// pinned release and materially misled every attempt before this one.
    /// </summary>
    private static WeightRecord CreateWeightRecord(Context context, Java.Time.Instant instant, Mass weight)
    {
        using var classLoader = context.ClassLoader!;
        using var weightRecordClass = RequireClass(classLoader.LoadClass(WeightRecordJavaClassName), WeightRecordJavaClassName);
        using var instantClass = RequireClass(classLoader.LoadClass(InstantJavaClassName), InstantJavaClassName);
        using var zoneOffsetClass = RequireClass(classLoader.LoadClass(ZoneOffsetJavaClassName), ZoneOffsetJavaClassName);
        using var massClass = RequireClass(classLoader.LoadClass(MassJavaClassName), MassJavaClassName);
        using var metadataClass = RequireClass(classLoader.LoadClass(MetadataJavaClassName), MetadataJavaClassName);

        using var metadata = CreateMetadata(classLoader, metadataClass);

        using var constructor = weightRecordClass.GetConstructor(instantClass, zoneOffsetClass, massClass, metadataClass)
            ?? throw new InvalidOperationException(
                $"{WeightRecordJavaClassName}(Instant, ZoneOffset, Mass, Metadata) constructor was not found via reflection.");
        var result = constructor.NewInstance(instant, null, weight, metadata);
        return (WeightRecord)result!;
    }

    /// <summary>
    /// Builds a real <c>Metadata</c> instance for a manually-captured scale reading, by calling
    /// its real public constructor directly - see the longer explanation on
    /// <see cref="CreateWeightRecord"/> for how its actual shape (for the exact
    /// <c>connect-client:1.1.0-alpha07</c> version this project pins) was confirmed:
    /// <c>Metadata(id: String, dataOrigin: DataOrigin, lastModifiedTime: Instant, clientRecordId:
    /// String?, clientRecordVersion: Long, device: Device?, recordingMethod: Int)</c>, with no
    /// <c>@JvmOverloads</c>, so every parameter must be supplied explicitly. Values passed here
    /// match the Kotlin-side defaults exactly (an empty `id`/`DataOrigin("")`, `Instant.EPOCH`,
    /// no client record id/version, no device), except <c>recordingMethod</c>, which is set to
    /// the real <c>RECORDING_METHOD_MANUAL_ENTRY</c> constant (confirmed value <c>3</c>) instead
    /// of its own default (<c>RECORDING_METHOD_UNKNOWN</c>), since that's an accurate, genuinely
    /// better description of how this app's readings are actually captured.
    /// </summary>
    private static Java.Lang.Object CreateMetadata(Java.Lang.ClassLoader classLoader, Java.Lang.Class metadataClass)
    {
        using var stringClass = RequireClass(classLoader.LoadClass(StringJavaClassName), StringJavaClassName);
        using var dataOriginClass = RequireClass(classLoader.LoadClass(DataOriginJavaClassName), DataOriginJavaClassName);
        using var instantClass = RequireClass(classLoader.LoadClass(InstantJavaClassName), InstantJavaClassName);
        using var deviceClass = RequireClass(classLoader.LoadClass(DeviceJavaClassName), DeviceJavaClassName);
        using var longType = Java.Lang.Long.Type ?? throw new InvalidOperationException("Java.Lang.Long.Type (primitive long Class) was null.");
        using var intType = Java.Lang.Integer.Type ?? throw new InvalidOperationException("Java.Lang.Integer.Type (primitive int Class) was null.");

        // DataOrigin(packageName: String) - a plain, single-argument public constructor with no
        // default of its own, matching Metadata's own default of DataOrigin("").
        using var dataOriginConstructor = dataOriginClass.GetConstructor(stringClass)
            ?? throw new InvalidOperationException($"{DataOriginJavaClassName}(String) constructor was not found via reflection.");
        using var emptyPackageName = new Java.Lang.String(string.Empty);
        using var dataOrigin = dataOriginConstructor.NewInstance(emptyPackageName)
            ?? throw new InvalidOperationException($"{DataOriginJavaClassName}(String) constructor returned null.");

        using var epoch = Java.Time.Instant.Epoch ?? throw new InvalidOperationException("Java.Time.Instant.Epoch was null.");

        using var metadataConstructor = metadataClass.GetConstructor(
            stringClass, dataOriginClass, instantClass, stringClass, longType, deviceClass, intType)
            ?? throw new InvalidOperationException(
                $"{MetadataJavaClassName}(String, DataOrigin, Instant, String, long, Device, int) constructor was not found via reflection.");
        using var emptyId = new Java.Lang.String(string.Empty);
        using var clientRecordVersion = new Java.Lang.Long(0L);
        using var recordingMethod = new Java.Lang.Integer(RecordingMethodManualEntry);
        var result = metadataConstructor.NewInstance(
            emptyId, dataOrigin, epoch, null, clientRecordVersion, null, recordingMethod);
        return result ?? throw new InvalidOperationException($"{MetadataJavaClassName} constructor returned null.");
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
