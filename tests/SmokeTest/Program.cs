using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace GpgCursorPatcher.SmokeTest;

/// <summary>
/// Patches a throwaway copy of crosvm.exe and reads the cursor back out, so a
/// change to the resource layout fails here rather than on someone's install.
///
///     dotnet run --project tests/SmokeTest -- [path-to-a-cursor-image]
///
/// With no argument it generates its own test image, so it runs anywhere Play
/// Games is installed. WPF imaging needs a single-threaded apartment.
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        // Keep the real backup and remembered image out of this. Must be set
        // before CursorPatcher is first touched, since it reads this once.
        var stateDir = Path.Combine(Path.GetTempPath(), "gpg-cursor-smoketest");
        Environment.SetEnvironmentVariable("GPG_CURSOR_STATE_DIR", stateDir);

        var exe = CursorPatcher.DefaultExePath;
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"Play Games is not installed at {exe} -- nothing to test against.");
            return 2;
        }

        var copy = Path.Combine(Path.GetTempPath(), "crosvm.smoketest.exe");
        File.Copy(exe, copy, overwrite: true);

        try
        {
            var stock = CursorPatcher.ReadCurrentCursor(copy);
            Check("the stock cursor reads back", stock is not null);
            Check("the stock cursor is 64x64", stock is { PixelWidth: 64, PixelHeight: 64 });

            int hotspotX, hotspotY;
            BitmapSource image;
            string name;

            if (args.Length > 0)
            {
                image = CursorPatcher.LoadImage(args[0], out hotspotX, out hotspotY);
                name = Path.GetFileName(args[0]);
                Console.WriteLine($"  using {name}, hotspot {hotspotX},{hotspotY}");
            }
            else
            {
                (image, hotspotX, hotspotY) = MakeTestImage();
                name = "generated";
            }

            Check("the replacement is scaled to 64x64", image is { PixelWidth: 64, PixelHeight: 64 });
            Check("the hotspot stays inside the image", hotspotX is >= 0 and < 64 && hotspotY is >= 0 and < 64);

            // The patch has to survive a full write/read cycle through the PE file,
            // which is the part that actually breaks if the resource layout is wrong.
            CursorPatcher.Apply(copy, image, hotspotX, hotspotY, name);

            var readBack = CursorPatcher.ReadCurrentCursor(copy);
            Check("the patched cursor reads back", readBack is not null);
            Check("the patched cursor is 64x64", readBack is { PixelWidth: 64, PixelHeight: 64 });
            Check("the patched cursor differs from the stock one", !SamePixels(stock!, readBack!));
            Check("the pixels survive the round trip", SamePixels(image, readBack!));

            var status = CursorPatcher.GetStatus(copy, out _);
            Check("the copy reports itself patched", status == InstallStatus.Patched);
        }
        finally
        {
            File.Delete(copy);
            if (Directory.Exists(stateDir)) Directory.Delete(stateDir, recursive: true);
        }

        Console.WriteLine(_failures == 0 ? "\nall checks passed" : $"\n{_failures} check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    private static bool SamePixels(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        var pa = new byte[a.PixelWidth * a.PixelHeight * 4];
        var pb = new byte[pa.Length];
        a.CopyPixels(pa, a.PixelWidth * 4, 0);
        b.CopyPixels(pb, b.PixelWidth * 4, 0);
        return pa.AsSpan().SequenceEqual(pb);
    }

    /// <summary>A 64x64 image written straight to a temp png, so no drawing stack is involved.</summary>
    private static (BitmapSource Image, int X, int Y) MakeTestImage()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                bool inside = x < size - y;             // a triangle in the top-left corner
                pixels[o + 0] = 0;                      // B
                pixels[o + 1] = 0;                      // G
                pixels[o + 2] = (byte)(inside ? 255 : 0); // R
                pixels[o + 3] = (byte)(inside ? 255 : 0); // A
            }

        var bmp = BitmapSource.Create(size, size, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return (bmp, 0, 0);
    }
}
