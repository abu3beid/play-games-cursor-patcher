using System.Windows;
using System.Windows.Threading;

namespace GpgCursorPatcher;

public partial class App : Application
{
    public App()
    {
        // A patcher that dies on an unexpected error leaves the user with no idea
        // whether the executable was left half-written, so always say something.
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Play Games Cursor Patcher",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
