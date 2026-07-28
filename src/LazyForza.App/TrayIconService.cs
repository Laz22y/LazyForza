using System.Drawing;
using Forms = System.Windows.Forms;

namespace LazyForza.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Icon icon;

    public TrayIconService(
        string runtimeMode,
        string listener,
        Action showMainWindow,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showMainWindow);
        ArgumentNullException.ThrowIfNull(exitApplication);

        icon = LoadApplicationIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(
            $"当前：{runtimeMode} · 监听 {listener}")
        {
            Enabled = false
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        var showItem = new Forms.ToolStripMenuItem("显示主界面");
        showItem.Click += (_, _) => showMainWindow();
        menu.Items.Add(showItem);
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exitApplication();
        menu.Items.Add(exitItem);

        notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "LazyForza",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => showMainWindow();
    }

    public void ShowMinimizedNotice()
    {
        notifyIcon.BalloonTipTitle = "LazyForza 仍在运行";
        notifyIcon.BalloonTipText = "已最小化到托盘。双击托盘图标可重新打开主界面。";
        notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        icon.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Icon LoadApplicationIcon()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var extracted = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (extracted is not null) return (Icon)extracted.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
