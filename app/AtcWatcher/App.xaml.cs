using System.Windows;
using System.Windows.Threading;
using AtcWatcher.Core;
using MessageBox = System.Windows.MessageBox;

namespace AtcWatcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write($"Fatal: {args.ExceptionObject}");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write($"Unhandled UI error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "ATC Watcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
