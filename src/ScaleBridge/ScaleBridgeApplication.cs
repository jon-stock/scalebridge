using Android.App;
using Android.Runtime;
using ScaleBridge.Status;

namespace ScaleBridge;

/// <summary>
/// Installs a global crash handler as early as possible - <c>Application.OnCreate</c> runs before
/// any Activity's <c>OnCreate</c>, so this catches crashes even during <see cref="MainActivity"/>'s
/// own startup (e.g. Android tooling/Material theming issues, or a Health Connect binding call
/// throwing) - see <see cref="Status.CrashLog"/> for why this exists and how to view a captured
/// crash (a "Last crash" card appears on the main screen).
///
/// Deliberately does not attempt to suppress/recover from the crash itself (no `e.Handled = true`)
/// - only to record it first. Recovering from an unknown broken state is riskier than letting the
/// normal crash behaviour continue after logging.
/// </summary>
[Application]
public class ScaleBridgeApplication : Application
{
    public ScaleBridgeApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => CrashLog.Record(this, e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CrashLog.Record(this, ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Record(this, e.Exception);
            e.SetObserved();
        };
    }
}
