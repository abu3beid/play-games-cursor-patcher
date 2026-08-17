using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GpgCursorPatcher;

/// <summary>What the app did last time, so an update can be noticed and undone.</summary>
public sealed class PatchState
{
    public string PatchedHash { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int HotspotX { get; set; }
    public int HotspotY { get; set; }
    public string ExeVersion { get; set; } = "";
    public DateTime PatchedAt { get; set; }
}

public enum InstallStatus { NotFound, NeverPatched, Patched, NeedsReapply }

/// <summary>
/// Swaps the cursor Google Play Games draws over a game.
///
/// Play Games offers two under Mouse pointer -- Standard, which is the plain
/// Windows pointer, and Large -- with no way to supply your own. Large is an
/// ordinary Win32 CURSOR resource (id 6, described by GROUP_CURSOR id 1) inside
/// crosvm.exe, the process that hosts the VM and owns the game window. Replacing
/// that resource replaces the cursor.
///
/// Its protobuf enum also names CURSOR_TYPE_GREEN_64X64, but no green asset is
/// shipped and crosvm.exe carries exactly one RT_CURSOR, so id 6 is the only
/// cursor there is to replace.
///
/// The setting still has to be on Large; on Standard the game draws the Windows
/// pointer and never reads this resource.
/// </summary>
public static class CursorPatcher
{
    public const int Size = 64;

    private const int RT_CURSOR = 1;
    private const int RT_GROUP_CURSOR = 12;
    private const int CursorId = 6;
    private const int GroupId = 1;
    private const ushort LangId = 1033;

    // The backup and the remembered image live here. Overridable so the smoke
    // test can run without trampling the real backup of someone's install.
    private static readonly string StateDir =
        Environment.GetEnvironmentVariable("GPG_CURSOR_STATE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gpg-cursor-patch");

    public static string BackupPath => Path.Combine(StateDir, "crosvm.original.exe");
    private static string SavedImagePath => Path.Combine(StateDir, "cursor.png");
    private static string StateFilePath => Path.Combine(StateDir, "state.json");

    public static string DefaultExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        @"Google\Play Games\current\emulator\crosvm.exe");

    // ------------------------------------------------------------- win32
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UpdateResourceW(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[]? data, uint cb);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr h, bool discard);

    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    // ------------------------------------------------------------- state
    public static PatchState? LoadState()
    {
        try
        {
            return File.Exists(StateFilePath)
                ? JsonSerializer.Deserialize<PatchState>(File.ReadAllText(StateFilePath))
                : null;
        }
        catch { return null; }
    }

    private static void SaveState(PatchState s)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StateFilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static InstallStatus GetStatus(string exePath, out string version)
    {
        version = "";
        if (!File.Exists(exePath)) return InstallStatus.NotFound;
        version = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "";

        var state = LoadState();
        if (state is null) return InstallStatus.NeverPatched;
        // A hash that no longer matches means Play Games replaced the file, which
        // puts the stock cursor back and makes the saved image worth reapplying.
        return Sha256(exePath) == state.PatchedHash ? InstallStatus.Patched : InstallStatus.NeedsReapply;
    }

    /// <summary>The image used last time, so a reapply after an update needs no input.</summary>
    public static BitmapSource? LoadSavedImage() =>
        File.Exists(SavedImagePath) ? LoadImage(SavedImagePath, out _, out _) : null;

    public static bool HasSavedImage => File.Exists(SavedImagePath);

    /// <summary>Whether crosvm.exe can actually be rewritten right now.</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Names of Play Games processes holding the install open, used to explain a
    /// failed write. The service outlives the game window, so "I closed it" is
    /// usually not enough.
    /// </summary>
    public static string[] RunningProcesses(string exePath)
    {
        // Only walk up past the folder holding crosvm.exe when it is the Play
        // Games layout (...\current\emulator\crosvm.exe). Climbing blindly can
        // land on something like AppData\Local and match half the machine.
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (dir is null) return [];
        var root = Path.GetFileName(dir).Equals("emulator", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) ?? dir
            : dir;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file is not null && file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    names.Add(p.ProcessName);
            }
            catch { /* most processes refuse MainModule; they are not ours anyway */ }
            finally { p.Dispose(); }
        }
        return [.. names];
    }

    // ------------------------------------------------------------- reading
    private static byte[]? ReadResource(string exePath, int type, int id)
    {
        var lib = LoadLibraryExW(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (lib == IntPtr.Zero) return null;
        try
        {
            var res = FindResourceW(lib, id, type);
            if (res == IntPtr.Zero) return null;
            var size = SizeofResource(lib, res);
            var ptr = LockResource(LoadResource(lib, res));
            if (ptr == IntPtr.Zero || size == 0) return null;
            var buf = new byte[size];
            Marshal.Copy(ptr, buf, 0, (int)size);
            return buf;
        }
        finally { FreeLibrary(lib); }
    }

    /// <summary>The cursor currently inside crosvm.exe, for the "current" preview.</summary>
    public static BitmapSource? ReadCurrentCursor(string exePath)
    {
        var res = ReadResource(exePath, RT_CURSOR, CursorId);
        // hotspot (4) + BITMAPINFOHEADER (40), then bottom-up BGRA, then the AND mask.
        if (res is null || res.Length < 44) return null;

        int width = BitConverter.ToInt32(res, 8);
        int height = BitConverter.ToInt32(res, 12) / 2;
        short bpp = BitConverter.ToInt16(res, 18);
        if (bpp != 32 || width <= 0 || height <= 0) return null;
        if (res.Length < 44 + width * height * 4) return null;

        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(res, 44 + (height - 1 - y) * width * 4, pixels, y * width * 4, width * 4);

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------- loading
    /// <summary>
    /// Loads any image and scales it to 64x64. A .cur is parsed by hand: the two
    /// words a .ico uses for colour planes and bit count hold the hotspot instead,
    /// so WPF's decoders cannot be used and the hotspot comes back as an out value.
    /// </summary>
    public static BitmapSource LoadImage(string path, out int hotspotX, out int hotspotY)
    {
        hotspotX = hotspotY = 0;
        BitmapSource source;

        if (Path.GetExtension(path).Equals(".cur", StringComparison.OrdinalIgnoreCase))
        {
            var raw = File.ReadAllBytes(path);
            if (raw.Length < 22 || BitConverter.ToUInt16(raw, 2) != 2)
                throw new InvalidDataException("This is not a cursor (.cur) file.");

            hotspotX = BitConverter.ToUInt16(raw, 10);
            hotspotY = BitConverter.ToUInt16(raw, 12);
            int length = BitConverter.ToInt32(raw, 14);
            int offset = BitConverter.ToInt32(raw, 18);
            if (offset + length > raw.Length) throw new InvalidDataException("The cursor file is truncated.");

            var entry = new byte[length];
            Buffer.BlockCopy(raw, offset, entry, 0, length);
            source = entry is [0x89, 0x50, .., ] ? Decode(new MemoryStream(entry)) : DecodeDib(entry);
        }
        else
        {
            source = Decode(File.OpenRead(path));
        }

        // The hotspot came from the file's own coordinate space. Scaling a 48x48
        // cursor up to 64x64 moves the pixel it referred to, so move it with the
        // image or clicks land off the point.
        if (source.PixelWidth != Size && source.PixelWidth > 0)
            hotspotX = (int)Math.Round(hotspotX * (double)Size / source.PixelWidth);
        if (source.PixelHeight != Size && source.PixelHeight > 0)
            hotspotY = (int)Math.Round(hotspotY * (double)Size / source.PixelHeight);

        return Scale(source, Size);
    }

    private static BitmapSource Decode(Stream stream)
    {
        using (stream)
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
    }

    /// <summary>A classic (uncompressed) cursor entry: BITMAPINFOHEADER, bottom-up pixels, mask.</summary>
    private static BitmapSource DecodeDib(byte[] dib)
    {
        int width = BitConverter.ToInt32(dib, 4);
        int height = BitConverter.ToInt32(dib, 8) / 2;
        short bpp = BitConverter.ToInt16(dib, 14);
        int headerSize = BitConverter.ToInt32(dib, 0);

        if (bpp != 32)
            throw new NotSupportedException(
                $"This cursor is {bpp}-bit. Only 32-bit cursors can be read directly -- save it as a .png and use that instead.");

        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(dib, headerSize + (height - 1 - y) * width * 4, pixels, y * width * 4, width * 4);

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource Scale(BitmapSource source, int size)
    {
        if (source.PixelWidth == size && source.PixelHeight == size) return source;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, size, size));

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        // RenderTargetBitmap is premultiplied; the cursor format wants straight alpha.
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    // ------------------------------------------------------------- building
    private static byte[] BuildCursorResource(BitmapSource image, int hotspotX, int hotspotY)
    {
        var bgra = new byte[Size * Size * 4];
        image.CopyPixels(bgra, Size * 4, 0);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((ushort)hotspotX);
        w.Write((ushort)hotspotY);

        // BITMAPINFOHEADER. The height is doubled because a cursor DIB stacks the
        // colour image and the AND mask.
        w.Write(40);
        w.Write(Size);
        w.Write(Size * 2);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);          // BI_RGB
        w.Write(0);          // biSizeImage
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        for (int y = Size - 1; y >= 0; y--)         // bottom-up
            w.Write(bgra, y * Size * 4, Size * 4);

        // AND mask, all zero: the alpha channel does the transparency and a zeroed
        // mask means "invert nothing".
        w.Write(new byte[Size * Size / 8]);

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildGroupResource(int bytesInRes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);           // reserved
        w.Write((ushort)2);           // type: cursor
        w.Write((ushort)1);           // one image
        w.Write((ushort)Size);
        w.Write((ushort)(Size * 2));  // doubled height, as in the header
        w.Write((ushort)1);           // planes
        w.Write((ushort)32);          // bit count
        w.Write(bytesInRes);
        w.Write((ushort)CursorId);
        w.Flush();
        return ms.ToArray();
    }

    // ------------------------------------------------------------- applying
    public static void Apply(string exePath, BitmapSource image, int hotspotX, int hotspotY, string sourceName)
    {
        EnsureWritable(exePath);

        Directory.CreateDirectory(StateDir);
        var state = LoadState();

        // Back up only a genuine Google build. If the hash still matches what was
        // written last time, the existing backup is the original and overwriting it
        // would replace it with an already-patched file.
        var currentHash = Sha256(exePath);
        if (state is null || currentHash != state.PatchedHash || !File.Exists(BackupPath))
            File.Copy(exePath, BackupPath, overwrite: true);

        var cursor = BuildCursorResource(image, hotspotX, hotspotY);
        var group = BuildGroupResource(cursor.Length);

        var handle = BeginUpdateResourceW(exePath, false);
        if (handle == IntPtr.Zero) throw new Win32Exception("Could not open crosvm.exe for editing");
        try
        {
            if (!UpdateResourceW(handle, RT_CURSOR, CursorId, LangId, cursor, (uint)cursor.Length))
                throw new Win32Exception("Could not write the cursor resource");
            if (!UpdateResourceW(handle, RT_GROUP_CURSOR, GroupId, LangId, group, (uint)group.Length))
                throw new Win32Exception("Could not write the cursor group");
        }
        catch
        {
            EndUpdateResourceW(handle, true);
            throw;
        }
        if (!EndUpdateResourceW(handle, false))
            throw new Win32Exception("Could not commit the change");

        SaveImage(image);
        SaveState(new PatchState
        {
            PatchedHash = Sha256(exePath),
            SourceName = sourceName,
            HotspotX = hotspotX,
            HotspotY = hotspotY,
            ExeVersion = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "",
            PatchedAt = DateTime.Now
        });
    }

    public static void Restore(string exePath)
    {
        if (!File.Exists(BackupPath)) throw new FileNotFoundException("There is no backup to restore from.");
        EnsureWritable(exePath);

        File.Copy(BackupPath, exePath, overwrite: true);
        if (File.Exists(StateFilePath)) File.Delete(StateFilePath);
    }

    /// <summary>
    /// The one precondition that matters. Only when the write is refused is it
    /// worth naming processes, which is both slow and easy to get wrong.
    /// </summary>
    private static void EnsureWritable(string exePath)
    {
        if (CanWrite(exePath)) return;

        var running = RunningProcesses(exePath);
        throw new IOException(running.Length > 0
            ? $"Play Games is still running ({string.Join(", ", running)}). Quit it from the system tray icon, then try again."
            : $"{Path.GetFileName(exePath)} is in use or not writable. Quit Play Games from the system tray icon and make sure this is running as administrator.");
    }

    private static void SaveImage(BitmapSource image)
    {
        Directory.CreateDirectory(StateDir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(SavedImagePath);
        encoder.Save(stream);
    }

    private sealed class Win32Exception(string message)
        : Exception($"{message} (Windows error {Marshal.GetLastWin32Error()}).");
}
