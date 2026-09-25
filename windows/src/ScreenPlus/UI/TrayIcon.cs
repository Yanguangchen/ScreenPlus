using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ScreenPlus.UI;

/// <summary>
/// A notification-area icon while recording, so you can stop without finding the toolbar
/// (the Mac app's menu bar item).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(AppModel model)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Stop Recording", null, (_, _) => model.StopRecording());
        menu.Items.Add("Discard Recording", null, (_, _) => model.DiscardRecording());
        _icon = new Forms.NotifyIcon
        {
            Text = "ScreenPlus is recording",
            ContextMenuStrip = menu,
            Visible = false,
        };
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/ScreenPlus;component/Assets/AppIcon.ico"))?.Stream)
        {
            if (stream != null) _icon.Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        // A left click opens the menu too.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_icon, null);
        };
    }

    public void Show() => _icon.Visible = true;
    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
