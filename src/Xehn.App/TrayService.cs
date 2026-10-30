using System.ComponentModel;
using Icon = System.Drawing.Icon;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Xehn.App;

public sealed class TrayService : IDisposable
{
    private readonly MainViewModel model;
    private readonly ContextMenu menu = new();
    private readonly MenuItem connection = new();
    private readonly Forms.NotifyIcon icon;
    private readonly Icon artwork;
    public ContextMenu ContextMenu => menu;
    public TrayService(MainViewModel model, Action show, Func<Task> exit, bool visible = true)
    {
        this.model = model;
        using var stream = Application.GetResourceStream(new Uri(Branding.IconResource)).Stream;
        artwork = new Icon(stream, new System.Drawing.Size(32, 32));
        menu.Style = (Style)Application.Current.FindResource("TrayContextMenu");
        menu.ItemContainerStyle = (Style)Application.Current.FindResource("TrayMenuItem");
        var display = new MenuItem { Header = "显示主界面" }; display.Click += (_, _) => show();
        connection.Click += async (_, _) => { if (model.CanToggleFromTray) await model.ToggleAsync(); };
        var quit = new MenuItem { Header = "退出" }; quit.Click += async (_, _) => await exit();
        menu.Items.Add(display); menu.Items.Add(connection); menu.Items.Add(quit);
        icon = new Forms.NotifyIcon { Icon = artwork, Text = "XEHN", Visible = visible };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
            else if (e.Button == Forms.MouseButtons.Right)
            {
                Update(null, new(""));
                menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true;
            }
        };
        menu.Opened += (_, _) =>
        {
            // A foreground popup dismisses correctly when the user clicks away,
            // including when the owning main window is hidden in the tray.
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
            menu.Focus();
        };
        model.PropertyChanged += Update;
        App.ThemeChanged += RefreshTheme;
        RefreshTheme();
        Update(null, new(""));
    }
    private void RefreshTheme()
    {
        // A closed ContextMenu has no owner visual tree. Give its popup local
        // theme resources too, so it updates even while hidden in the tray.
        foreach (var key in new[] { "PageBrush", "CardBrush", "InkBrush", "TintBrush", "LineBrush" }) menu.Resources[key] = Application.Current.FindResource(key);
        menu.Background = (Brush)Application.Current.FindResource("PageBrush");
        menu.Foreground = (Brush)Application.Current.FindResource("InkBrush");
        foreach (MenuItem item in menu.Items) item.Foreground = menu.Foreground;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private void Update(object? sender, PropertyChangedEventArgs e)
    {
        connection.Header = model.TrayConnectionLabel;
        connection.IsEnabled = model.CanToggleFromTray;
        icon.Text = "XEHN · " + model.Status;
    }
    public void Dispose()
    {
        model.PropertyChanged -= Update; App.ThemeChanged -= RefreshTheme; menu.IsOpen = false;
        icon.Visible = false; icon.Dispose(); artwork.Dispose();
    }
}
