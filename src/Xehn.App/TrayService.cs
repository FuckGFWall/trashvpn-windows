using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Xehn.App;

public sealed class TrayService : IDisposable
{
    private readonly MainViewModel model;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem connection = new();
    private readonly Forms.NotifyIcon icon;
    private readonly Icon artwork;
    public Forms.ContextMenuStrip ContextMenu => menu;
    public TrayService(MainViewModel model, Action show, Func<Task> exit, bool visible = true)
    {
        this.model = model;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Xehn.App;component/Assets/logo.ico")).Stream;
        artwork = new Icon(stream, new System.Drawing.Size(32, 32));
        menu.Items.Add("退出", null, async (_, _) => await exit());
        connection.Click += async (_, _) => { if (model.CanToggleFromTray) await model.ToggleAsync(); };
        menu.Items.Add(connection);
        menu.Items.Add("显示主界面", null, (_, _) => show());
        icon = new Forms.NotifyIcon { Icon = artwork, Text = "XEHN", ContextMenuStrip = menu, Visible = visible };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        model.PropertyChanged += Update;
        menu.Opening += (_, _) => Update(null, new(""));
        Update(null, new(""));
    }
    private void Update(object? sender, PropertyChangedEventArgs e)
    {
        connection.Text = model.TrayConnectionLabel;
        connection.Enabled = model.CanToggleFromTray;
        icon.Text = "XEHN · " + model.Status;
    }
    public void Dispose()
    {
        model.PropertyChanged -= Update;
        icon.Visible = false; icon.Dispose(); menu.Dispose(); artwork.Dispose();
    }
}
