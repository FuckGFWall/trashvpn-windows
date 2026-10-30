using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Xehn.App;

public static class WindowPresentation
{
    // Work areas and native window bounds are physical pixels; preferred size
    // is in WPF DIPs. Include the entire native frame, not just client content.
    public static Rect FitBounds(Rect workArea, Size preferredSize, double scale, Point? position = null)
    {
        var margin = Math.Min(12 * scale, Math.Min(workArea.Width, workArea.Height) / 10);
        var width = Math.Min(preferredSize.Width * scale, workArea.Width - 2 * margin);
        var height = Math.Min(preferredSize.Height * scale, workArea.Height - 2 * margin);
        var left = position?.X ?? workArea.Left + (workArea.Width - width) / 2;
        var top = position?.Y ?? workArea.Top + (workArea.Height - height) / 2;
        return new Rect(Math.Clamp(left, workArea.Left + margin, workArea.Right - margin - width),
            Math.Clamp(top, workArea.Top + margin, workArea.Bottom - margin - height), width, height);
    }

    public static void FitToWorkArea(Window window, bool center)
    {
        if (window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var current)) return;
        var scale = GetDpiForWindow(handle) / 96d;
        if (scale <= 0) scale = 1;
        var bounds = FitBounds(new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
            new Size(window.Width, window.Height), scale, center ? null : new Point(current.Left, current.Top));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.MinWidth = Math.Min(window.MinWidth, bounds.Width / scale);
        window.MinHeight = Math.Min(window.MinHeight, bounds.Height / scale);
        window.Width = bounds.Width / scale; window.Height = bounds.Height / scale;
        SetWindowPos(handle, 0, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x14); // NOZORDER | NOACTIVATE
    }

    public static (bool DarkModeApplied, bool CaptionColorsApplied) ApplyTitleBar(Window window, bool dark, Color background, Color foreground)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return (false, false);
        // Documented DWM attributes: native buttons retain their standard
        // behavior, while the caption follows the app rather than OS theme.
        var mode = dark ? 1 : 0;
        var modeResult = DwmSetWindowAttribute(handle, 20, ref mode, sizeof(int));
        var caption = ColorRef(background); var text = ColorRef(foreground);
        // Caption/text colors require Windows 11; Windows 10 still follows
        // light/dark via attribute 20 when the newer colors are unsupported.
        var captionResult = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        var textResult = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        SetWindowPos(handle, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, no move/size/activate/z-order
        return (modeResult == 0, captionResult == 0 && textResult == 0);
    }

    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
