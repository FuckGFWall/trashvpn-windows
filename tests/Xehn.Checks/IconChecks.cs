using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xehn.App;

internal static class IconChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        using var original = Application.GetResourceStream(new Uri("pack://application:,,,/Xehn.App;component/Assets/logo.ico")).Stream;
        using var rounded = Application.GetResourceStream(new Uri(Branding.IconResource)).Stream;
        using var bytes = new MemoryStream(); rounded.CopyTo(bytes);
        check(IconArtwork.RoundIco(original).SequenceEqual(bytes.ToArray()), "every displayed / native icon uses the reproducible rounded original artwork");
        original.Position = 0; var before = Frames(original); bytes.Position = 0; var after = Frames(bytes);
        check(before.Count == after.Count && before.Zip(after).All(p => p.First.PixelWidth == p.Second.PixelWidth), "rounded ICO retains every original resolution");
        check(after.All(f => CornerAlpha(f).All(a => a < 200)), "rounded icons have antialiased transparent corners at every resolution");
        check(before.Zip(after).All(p => InteriorMatches(p.First, p.Second)), "corner mask preserves original interior pixels without redrawing the logo");
        Save(before.MaxBy(f => f.PixelWidth)!, Path.Combine(output, "logo-original-preview.png"));
        Save(after.MaxBy(f => f.PixelWidth)!, Path.Combine(output, "logo-rounded-preview.png"));
    }
    private static List<BitmapSource> Frames(Stream source)
    {
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, true);
        reader.ReadBytes(4); var count = reader.ReadUInt16(); var entries = new List<(uint Size, uint Offset)>();
        for (var i = 0; i < count; i++) { reader.ReadBytes(8); entries.Add((reader.ReadUInt32(), reader.ReadUInt32())); }
        var result = new List<BitmapSource>();
        foreach (var entry in entries) { source.Position = entry.Offset; using var png = new MemoryStream(reader.ReadBytes((int)entry.Size)); result.Add(BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)); }
        return result;
    }
    private static byte[] Pixels(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var result = new byte[frame.PixelWidth * frame.PixelHeight * 4]; converted.CopyPixels(result, frame.PixelWidth * 4, 0); return result;
    }
    private static byte[] CornerAlpha(BitmapSource frame)
    {
        var p = Pixels(frame); var w = frame.PixelWidth; var h = frame.PixelHeight;
        return [p[3], p[(w - 1) * 4 + 3], p[(h - 1) * w * 4 + 3], p[(w * h - 1) * 4 + 3]];
    }
    private static bool InteriorMatches(BitmapSource original, BitmapSource rounded)
    {
        var a = Pixels(original); var b = Pixels(rounded); var w = original.PixelWidth; var h = original.PixelHeight;
        var inset = (int)Math.Ceiling(Math.Min(w, h) * IconArtwork.CornerFraction);
        for (var y = inset; y < h - inset; y++) for (var x = 0; x < w; x++) for (var c = 0; c < 4; c++) if (a[(y * w + x) * 4 + c] != b[(y * w + x) * 4 + c]) return false;
        return true;
    }
    private static void Save(BitmapSource frame, string path) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame)); using var file = File.Create(path); png.Save(file); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[] large, [Out] IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static void CheckExecutable(Action<bool, string> check, string path)
    {
        var large = new IntPtr[1]; var small = new IntPtr[1];
        try
        {
            var extracted = ExtractIconEx(Path.GetFullPath(path), 0, large, small, 1);
            check(extracted > 0 && extracted != uint.MaxValue && large[0] != IntPtr.Zero && small[0] != IntPtr.Zero, $"built main EXE exposes both native icon sizes (extracted {extracted})");
            foreach (var handle in new[] { large[0], small[0] })
            {
                using var icon = System.Drawing.Icon.FromHandle(handle); using var bitmap = icon.ToBitmap();
                check(bitmap.GetPixel(0, 0).A < 200 && bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A < 200 && bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A > 0, $"built EXE {bitmap.Width}px icon contains the rounded transparent mask");
            }
        }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
    }
}
