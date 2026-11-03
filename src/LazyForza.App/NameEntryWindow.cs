using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LazyForza.App;

internal sealed class NameEntryWindow : AppWindow
{
    private readonly TextBox name;
    internal string Value => name.Text.Trim();

    internal NameEntryWindow(Window? owner, string title, string caption, string current)
    {
        if (owner is not null) Owner = owner;
        Title = AppLocalization.Literal(title);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(24) };
        var label = new TextBlock { Text = AppLocalization.Literal(caption), FontSize = 14,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(label);
        name = new TextBox { Text = current, MaxLength = 80, MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(name, label.Text);
        root.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = AppLocalization.Literal("取消"), IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = AppLocalization.Literal("保存"), IsDefault = true, MinWidth = 88,
            IsEnabled = !string.IsNullOrWhiteSpace(current) };
        save.SetResourceReference(BackgroundProperty, "AccentBrush");
        save.SetResourceReference(ForegroundProperty, "WindowBrush");
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        save.Click += (_, _) => { if (save.IsEnabled) DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(save); root.Children.Add(actions);
        Content = root;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    internal static string? Ask(Window owner, string title, string caption, string current)
    {
        var dialog = new NameEntryWindow(owner, title, caption, current);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}

internal static class TrackNameDialog
{
    internal static string? Ask(Window owner, string current) => NameEntryWindow.Ask(owner, "重命名赛道", "赛道名称", current);
}

internal static class VehicleProfileNameDialog
{
    internal static string? Ask(Window owner, string current) => NameEntryWindow.Ask(owner, "重命名车辆配置", "配置名称", current);
}
