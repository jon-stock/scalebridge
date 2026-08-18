using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace ScaleBridge;

/// <summary>
/// A deliberately minimal, dependency-free crash screen: launched directly by
/// <see cref="ScaleBridgeApplication"/>'s crash handler as soon as any unhandled exception is
/// caught, instead of relying on <see cref="MainActivity"/>'s own "Last crash" card - which only
/// helps if the app gets far enough to actually render that screen. If the crash happens very
/// early (e.g. during <c>MainActivity.OnCreate</c>, before it can show anything), that card would
/// never appear at all.
///
/// Everything here is built in plain code rather than from a layout resource or styled with
/// Material Components/the app's own theme: since we don't know *why* the app just crashed, this
/// avoids depending on anything that could plausibly be part of the problem (resource inflation,
/// theming, AndroidX Activity Result APIs, Health Connect, etc.) - only bare
/// Activity/View/TextView/Button, the most fundamental Android APIs there are.
/// </summary>
[Activity(Label = "ScaleBridge - crash details", Exported = false, Theme = "@android:style/Theme.Material.Light")]
public class CrashActivity : Activity
{
    public const string ExtraCrashText = "crash_text";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        string text = Intent?.GetStringExtra(ExtraCrashText) ?? "(no crash details were captured)";

        var scrollView = new ScrollView(this);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = (int)(16 * Resources!.DisplayMetrics!.Density);
        layout.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this) { Text = "ScaleBridge crashed" };
        title.SetTextSize(ComplexUnitType.Sp, 18);
        title.SetTypeface(title.Typeface, Android.Graphics.TypefaceStyle.Bold);
        layout.AddView(title);

        var subtitle = new TextView(this)
        {
            Text = "Details below - long-press to select and copy, or use Share to send this to someone. " +
                   $"A copy was also saved to a file: Android/data/{PackageName}/files/{Status.CrashLog.CrashFileName}",
        };
        subtitle.SetTextSize(ComplexUnitType.Sp, 13);
        subtitle.SetPadding(0, pad / 2, 0, pad);
        layout.AddView(subtitle);

        var shareButton = new Button(this) { Text = "Share this text" };
        shareButton.Click += (_, _) =>
        {
            var sendIntent = new Intent(Intent.ActionSend);
            sendIntent.SetType("text/plain");
            sendIntent.PutExtra(Intent.ExtraText, text);
            StartActivity(Intent.CreateChooser(sendIntent, "Share crash details"));
        };
        layout.AddView(shareButton);

        var body = new TextView(this) { Text = text };
        body.SetTextIsSelectable(true);
        body.SetTextSize(ComplexUnitType.Sp, 12);
        body.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);
        body.SetPadding(0, pad, 0, 0);
        layout.AddView(body);

        scrollView.AddView(layout);
        SetContentView(scrollView);
    }
}
