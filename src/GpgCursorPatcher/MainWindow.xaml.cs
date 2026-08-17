using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GpgCursorPatcher;

public partial class MainWindow : Window
{
    private readonly string _exePath = CursorPatcher.DefaultExePath;
    private BitmapSource? _image;
    private string _sourceName = "";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        // WPF leaves the title bar light on a dark window; this is the only way to
        // darken it. Ignored on Windows versions that do not know the attribute.
        SourceInitialized += (_, _) =>
        {
            const int useImmersiveDarkMode = 20;
            var on = 1;
            try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, useImmersiveDarkMode, ref on, sizeof(int)); }
            catch (DllNotFoundException) { }
        };
    }

    private void Refresh()
    {
        var status = CursorPatcher.GetStatus(_exePath, out var version);

        switch (status)
        {
            case InstallStatus.NotFound:
                Set(StatusText, "Not installed", (Brush)FindResource("Warn"));
                VersionText.Text = _exePath;
                ApplyButton.IsEnabled = BrowseButton.IsEnabled = RestoreButton.IsEnabled = false;
                return;

            case InstallStatus.Patched:
                Set(StatusText, "Patched", (Brush)FindResource("Good"));
                break;

            case InstallStatus.NeedsReapply:
                // The saved image is still on disk, so this is one click away.
                Set(StatusText, "Play Games updated — the stock cursor is back", (Brush)FindResource("Warn"));
                break;

            default:
                Set(StatusText, "Original cursor", (Brush)FindResource("InkMute"));
                break;
        }

        VersionText.Text = $"crosvm.exe {version}";
        CurrentPreview.Source = CursorPatcher.ReadCurrentCursor(_exePath);
        RestoreButton.IsEnabled = File.Exists(CursorPatcher.BackupPath);

        // Offer the last image straight away, so reapplying after an update needs
        // no digging for the file again.
        if (_image is null && CursorPatcher.HasSavedImage)
        {
            var state = CursorPatcher.LoadState();
            _image = CursorPatcher.LoadSavedImage();
            _sourceName = state?.SourceName ?? "saved image";
            if (_image is not null)
            {
                HotspotXBox.Text = (state?.HotspotX ?? 0).ToString();
                HotspotYBox.Text = (state?.HotspotY ?? 0).ToString();
                FileText.Text = $"{_sourceName} (remembered)";
            }
        }

        ShowReplacement();
    }

    private void ShowReplacement()
    {
        NewPreview.Source = _image;
        NoImageText.Visibility = _image is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = _image is not null;
    }

    private static void Set(System.Windows.Controls.TextBlock block, string text, Brush brush)
    {
        block.Text = text;
        block.Foreground = brush;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a cursor image",
            Filter = "Cursor images (*.png;*.ico;*.cur;*.bmp)|*.png;*.ico;*.cur;*.bmp|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _image = CursorPatcher.LoadImage(dialog.FileName, out var hx, out var hy);
            _sourceName = Path.GetFileName(dialog.FileName);
            HotspotXBox.Text = hx.ToString();
            HotspotYBox.Text = hy.ToString();
            FileText.Text = _sourceName;
            MessageText.Text = "Set the in-game cursor to one of the large options — on “default” the game draws the Windows pointer instead.";
            ShowReplacement();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not read that image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnHotspotChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { }

    private (int X, int Y) ReadHotspot()
    {
        int.TryParse(HotspotXBox.Text, out var x);
        int.TryParse(HotspotYBox.Text, out var y);
        return (Math.Clamp(x, 0, CursorPatcher.Size - 1), Math.Clamp(y, 0, CursorPatcher.Size - 1));
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        var (x, y) = ReadHotspot();

        try
        {
            CursorPatcher.Apply(_exePath, _image, x, y, _sourceName);
            MessageText.Text = $"Done — {_sourceName} is now the cursor, hotspot {x},{y}. Start Play Games and pick one of the large cursor options.";
            MessageText.Foreground = (Brush)FindResource("Good");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
            MessageText.Foreground = (Brush)FindResource("Warn");
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        try
        {
            CursorPatcher.Restore(_exePath);
            _image = null;
            _sourceName = "";
            FileText.Text = "PNG, ICO or CUR — scaled to 64×64";
            MessageText.Text = "The original crosvm.exe is back.";
            MessageText.Foreground = (Brush)FindResource("Good");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
            MessageText.Foreground = (Brush)FindResource("Warn");
        }
    }
}
