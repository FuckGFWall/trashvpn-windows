using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xehn.App;

public static class Branding
{
    // The upstream ICO stores every size as PNG. WPF's ICO decoder rejects
    // some such containers; decode the 256px PNG directly. All entry points
    // share the same subtly rounded alpha mask derived from the original ICO.
    public const string IconResource = "pack://application:,,,/Xehn.App;component/Assets/logo-rounded.ico";
    public static ImageSource Image { get; } = Load();
    private static ImageSource Load()
    {
        using var source = Application.GetResourceStream(new Uri(IconResource)).Stream;
        using var reader = new BinaryReader(source);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) throw new InvalidDataException("无效的品牌 ICO。");
        var count = reader.ReadUInt16();
        uint bestSize = 0, bestOffset = 0; var bestWidth = 0;
        for (var i = 0; i < count; i++)
        {
            int width = reader.ReadByte(); if (width == 0) width = 256;
            reader.ReadBytes(7);
            var size = reader.ReadUInt32(); var offset = reader.ReadUInt32();
            if (width > bestWidth) { bestWidth = width; bestSize = size; bestOffset = offset; }
        }
        source.Position = bestOffset;
        using var png = new MemoryStream(reader.ReadBytes(checked((int)bestSize)));
        var frame = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze(); return frame;
    }
}
