using System.Windows;

namespace PixelPin;

public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            _controller?.ReportUnhandled(args.Exception);
            _controller?.ShowError("Unexpected application error", args.Exception.Message);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _controller?.ReportUnhandled(exception);
            }
        };

        _controller = new AppController(this);
        _controller.Start(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
