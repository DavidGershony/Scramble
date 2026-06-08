using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.Logging;
using ReactiveUI.Avalonia;
using Scramble.Core.Logging;

namespace Scramble.MobileAndroid;

[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    private static readonly ILogger<MainApplication> _logger =
        LoggingConfiguration.CreateLogger<MainApplication>();

    protected MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Catch unhandled exceptions so the app doesn't silently die without a trace.
        // On Android, unobserved task exceptions and AppDomain exceptions can kill the
        // process without any user-visible feedback.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger.LogCritical(ex, "Unhandled AppDomain exception (isTerminating={IsTerminating})", args.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception — suppressing to prevent crash");
            args.SetObserved(); // Prevent the process from being torn down
        };
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // ReactiveUI.Avalonia 12.0.1 requires the callback overload (no parameterless variant).
        // The empty callback uses defaults; explicit configuration (exception handler etc.) can be
        // added when Android service composition is wired in.
        return base.CustomizeAppBuilder(builder)
            .UseReactiveUI(_ => { });
    }
}
