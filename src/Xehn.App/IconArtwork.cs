using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xehn.App;

// A deterministic alpha mask, not a redrawn logo. The upstream ICO remains
// untouched; the same derived frames serve native EXE / tray and WPF branding.
public static class IconArtwork
{
    public const double CornerFraction = .14;
    public static byte[] RoundIco(Stream original)
    {
        using var reader = new BinaryReader(original, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) throw new InvalidDataException("Invalid ICO header.");
        var count = reader.ReadUInt16();
        if (count is 0 or > 64) throw new InvalidDataException("Invalid ICO frame count.");
        var entries = new List<(int Width, int Height, uint Size, uint Offset)>();
        for (var i = 0; i < count; i++)
        {
            int width = reader.ReadByte(), height = reader.ReadByte();
            reader.ReadBytes(6);
            entries.Add((width == 0 ? 256 : width, height == 0 ? 256 : height, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        var frames = new List<byte[]>();
        foreach (var entry in entries)
        {
            original.Position = entry.Offset;
            using var png = new MemoryStream(reader.ReadBytes(checked((int)entry.Size)));
            var source = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (source.PixelWidth != entry.Width || source.PixelHeight != entry.Height) throw new InvalidDataException("ICO frame size mismatch.");
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = entry.Width * 4; var pixels = new byte[stride * entry.Height];
            converted.CopyPixels(pixels, stride, 0);
            var radius = Math.Min(entry.Width, entry.Height) * CornerFraction;
            for (var y = 0; y < entry.Height; y++)
                for (var x = 0; x < entry.Width; x++)
                {
                    var cx = x < radius ? radius : x >= entry.Width - radius ? entry.Width - radius : x + .5;
                    var cy = y < radius ? radius : y >= entry.Height - radius ? entry.Height - radius : y + .5;
                    if (cx == x + .5 || cy == y + .5) continue;
                    var inside = 0;
                    const int samples = 8;
                    for (var sy = 0; sy < samples; sy++)
                        for (var sx = 0; sx < samples; sx++)
                        {
                            var dx = x + (sx + .5) / samples - cx;
                            var dy = y + (sy + .5) / samples - cy;
                            if (dx * dx + dy * dy <= radius * radius) inside++;
                        }
                    var alpha = (y * entry.Width + x) * 4 + 3;
                    pixels[alpha] = (byte)((pixels[alpha] * inside + samples * samples / 2) / (samples * samples));
                }
            var bitmap = BitmapSource.Create(entry.Width, entry.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); frames.Add(output.ToArray());
        }
        using var ico = new MemoryStream(); using var writer = new BinaryWriter(ico);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write(count);
        var offset = checked((uint)(6 + 16 * count));
        for (var i = 0; i < count; i++)
        {
            writer.Write((byte)(entries[i].Width == 256 ? 0 : entries[i].Width)); writer.Write((byte)(entries[i].Height == 256 ? 0 : entries[i].Height));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(checked((uint)frames[i].Length)); writer.Write(offset); offset += checked((uint)frames[i].Length);
        }
        foreach (var frame in frames) writer.Write(frame);
        return ico.ToArray();
    }
}
