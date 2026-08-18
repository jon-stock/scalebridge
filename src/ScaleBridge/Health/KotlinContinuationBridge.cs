using Kotlin.Coroutines;

namespace ScaleBridge.Health;

/// <summary>
/// Bridges a single Kotlin `suspend fun` call into a blocking C# call, so
/// <see cref="HealthConnectWriter"/> can call androidx.health.connect's Kotlin
/// <c>HealthConnectClient.insertRecords(...)</c> API from C#/Java interop without running a real
/// Kotlin coroutine dispatcher of our own.
///
/// Background: a Kotlin `suspend fun` compiles to a plain JVM method whose last parameter is a
/// <c>kotlin.coroutines.Continuation</c>. Calling it synchronously from Java/C# is a well-known
/// interop trick: pass a trivial <c>Continuation</c> implementation with no real coroutine
/// context, and have its `resumeWith(Object)` callback signal a waiting thread with the result.
/// This MUST be called from a background thread (never the main thread) - which
/// <see cref="HealthConnectWriter"/>'s only caller (<c>ScaleConnectionService</c>) already
/// ensures via <c>Task.Run</c>.
///
/// On the JVM, a successful suspend-fun result is delivered to `resumeWith` as the plain return
/// value; a failure is delivered as a boxed `kotlin.Result` failure holder. We detect that case
/// generically via the Java class name rather than a generated C# binding type for
/// `kotlin.Result`, since inline/value classes like `Result` are one of the parts of the Kotlin
/// ABI most likely to bind unpredictably - see docs/PROTOCOL_CONFIRMATION.md for the full caveat
/// on this file (it is the one piece of this project that could not be verified with an actual
/// `dotnet build` in this environment).
/// </summary>
internal sealed class KotlinContinuationBridge<TResult> : Java.Lang.Object, IContinuation
    where TResult : Java.Lang.Object
{
    private readonly TaskCompletionSource<TResult?> _tcs = new();

    // Confirmed against a real build: the generated interface member is the property `Context`
    // (matching Kotlin's `val context: CoroutineContext`), not a `GetContext()` method.
    public ICoroutineContext Context => EmptyCoroutineContext.Instance!;

    public void ResumeWith(Java.Lang.Object? result)
    {
        if (result is not null && string.Equals(result.Class.Name, "kotlin.Result", StringComparison.Ordinal))
        {
            _tcs.TrySetException(ExtractFailure(result));
            return;
        }

        _tcs.TrySetResult(result as TResult);
    }

    /// <summary>Blocks the calling (background) thread until the suspend function completes.</summary>
    public TResult? AwaitResult(TimeSpan timeout)
    {
        if (!_tcs.Task.Wait(timeout))
            throw new TimeoutException("Timed out waiting for a Health Connect (Kotlin coroutine) call to complete.");

        return _tcs.Task.Result;
    }

    private static Exception ExtractFailure(Java.Lang.Object boxedKotlinResult)
    {
        try
        {
            // kotlin.Result.exceptionOrNull() is a static-ish extension on the boxed value in
            // Kotlin, but from Java it is called as an instance method on the boxed Result.
            // Deliberately avoided casting the result to Java.Lang.Throwable here (an `as`
            // conversion the compiler rejected as unrepresentable in this context) - ToString()
            // on the raw Java.Lang.Object is enough for a diagnostic message, and this failure
            // path is not used for any control-flow decision.
            using var method = boxedKotlinResult.Class.GetMethod("exceptionOrNull");
            var thrown = method.Invoke(boxedKotlinResult);
            return thrown is not null
                ? new InvalidOperationException($"Health Connect call failed: {thrown}")
                : new InvalidOperationException("Health Connect call failed with an unrecognised kotlin.Result failure.");
        }
        catch (Exception reflectionEx)
        {
            return new InvalidOperationException(
                "Health Connect call failed, and the failure reason could not be extracted via reflection.", reflectionEx);
        }
    }
}
