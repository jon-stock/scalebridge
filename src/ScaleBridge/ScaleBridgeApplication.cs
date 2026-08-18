using Android.App;
using Android.Content;
using Android.Runtime;
using ScaleBridge.Status;

namespace ScaleBridge;

/// <summary>
/// Installs a global crash handler as early as possible - <c>Application.OnCreate</c> runs before
/// any Activity's <c>OnCreate</c>, so this catches crashes even during <see cref="MainActivity"/>'s
/// own startup (e.g. Android tooling/Material theming issues, or a Health Connect binding call
/// throwing). On any unhandled exception, this immediately:
///
/// 1. Persists the exception (SharedPreferences + a plain text file) via <see cref="Status.CrashLog"/>.
/// 2. Launches <see cref="CrashActivity"/> directly, showing the details on screen straight away -
///    rather than relying on MainActivity's own "Last crash" card, which only helps if the app
///    gets far enough to render that screen. If MainActivity itself is what's crashing (e.g.
///    during its own OnCreate, before showing anything), that card would never appear; this does
///    not have that problem, since it is launched independently of whatever just crashed.
///
/// Deliberately does not attempt to suppress/recover from the crash itself (no `e.Handled = true`)
/// - only to record and surface it first. Recovering from an unknown broken state is riskier than
/// letting the normal crash behaviour continue afterwards.
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

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => HandleCrash(e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                HandleCrash(ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleCrash(e.Exception);
            e.SetObserved();
        };
    }

    private void HandleCrash(Exception exception)
    {
        CrashLog.Record(this, exception);

        try
        {
            var intent = new Intent(this, typeof(CrashActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
            intent.PutExtra(CrashActivity.ExtraCrashText, exception.ToString());
            StartActivity(intent);
        }
        catch
        {
            // If even this fails, the exception is still captured via CrashLog (SharedPreferences
            // + the crash_log.txt file) - nothing further we can safely do from inside a crash
            // handler that is itself failing.
        }
    }
}
