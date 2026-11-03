using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.App;

internal enum VehicleNameUpdateChoice { Later, Download, Ignore }

internal sealed class VehicleNameUpdateWindow : AppWindow
{
    internal VehicleNameUpdateChoice Choice { get; private set; } = VehicleNameUpdateChoice.Later;

    internal VehicleNameUpdateWindow(Window owner, VehicleNameCatalogInfo? current, VehicleNameRevision revision)
    {
        Owner = owner;
        Title = T("车辆名称库更新", "Vehicle name catalog update");
        Width = 640; Height = 550; MinWidth = 460; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("PanelBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = T("发现新的车辆名称数据", "New vehicle names are available"),
            FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13) });
        var details = new TextBox { Text = Details(current, revision), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 10, 12, 10), FontSize = 13, Background = (Brush)Application.Current.FindResource("CardBrush"),
            Foreground = Foreground, BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") };
        Grid.SetRow(details, 1); root.Children.Add(details);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
        Add(T("忽略本次更新", "Ignore this revision"), VehicleNameUpdateChoice.Ignore);
        Add(T("稍后更新", "Later"), VehicleNameUpdateChoice.Later);
        Add(T("下载并更新", "Download and update"), VehicleNameUpdateChoice.Download);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        Content = root;

        void Add(string label, VehicleNameUpdateChoice choice)
        {
            var button = new Button { Content = label, MinWidth = 76,
                IsCancel = choice == VehicleNameUpdateChoice.Later };
            if (choice == VehicleNameUpdateChoice.Download)
                button.SetResourceReference(StyleProperty, "PrimaryActionButton");
            button.Click += (_, _) => { Choice = choice; DialogResult = choice == VehicleNameUpdateChoice.Download; };
            buttons.Children.Add(button);
        }
    }

    internal static string Details(VehicleNameCatalogInfo? current, VehicleNameRevision revision) =>
        T("来源：HDR 社区车辆名称库（GitHub Gist）", "Source: HDR community vehicle names (GitHub Gist)") + "\n" +
        VehicleNameSnapshot.SourceUrl + "\n\n" +
        T("当前修订：", "Current revision: ") + (current?.Revision ?? "—") + "\n" +
        T("当前数据时间：", "Current data date: ") + (current is null ? "—" : Date(current.UpdatedAt)) + "\n" +
        T("当前收录车辆：", "Current vehicle count: ") + (current?.VehicleCount.ToString(CultureInfo.CurrentCulture) ?? "0") + "\n\n" +
        T("上游修订：", "Upstream revision: ") + revision.Revision + "\n" +
        T("上游提交时间：", "Upstream commit date: ") + Date(revision.UpdatedAt) + "\n" +
        T("最近提交的文本变动：", "Text changes in the latest commit: ") + $"+{revision.Additions} / −{revision.Deletions}" + "\n" +
        T("行数包含该文档集合中的变动，不等于新增或删除的车辆数量。具体名称将在下载后校验。",
            "Line counts cover the gist and are not counts of added or removed vehicles. Names will be validated after download.") + "\n\n" +
        T("确认后仅下载车辆编号与名称数据，保存在当前数据目录中，无需升级或重启 LazyForza。自动名称随数据刷新，手动命名保持不变。",
            "Download vehicle IDs and names to the current data directory. No LazyForza upgrade or restart is needed. Automatic names refresh; custom names stay unchanged.") + "\n\n" +
        T("稍后更新：下次启动仍会提示，也可在设置中检查。\n忽略本次更新：不再提示或下载这个修订；上游出现新修订时仍会提示。\n离线或下载失败时继续使用现有缓存或内置名称库。",
            "Later: ask again next launch, or check in Settings.\nIgnore this revision: do not prompt for or download this revision; future revisions are still offered.\nOffline or failed downloads keep the cached or bundled names available.");

    internal static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.CurrentCulture);
    internal static string T(string zh, string en) => AppLocalization.CurrentLanguage == "en" ? en : zh;
}

internal sealed class VehicleNameDownloadWindow : AppWindow
{
    internal VehicleNameDownloadWindow(Window owner, CancellationTokenSource cancellation)
    {
        Owner = owner;
        Title = VehicleNameUpdateWindow.T("更新车辆名称库", "Updating vehicle names");
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("PanelBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        panel.Children.Add(new TextBlock { Text = VehicleNameUpdateWindow.T("更新车辆名称库", "Updating vehicle names"),
            TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = VehicleNameUpdateWindow.T("正在下载并校验车辆名称…", "Downloading and validating vehicle names…"),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 7, 0, 10) });
        var progress = new ProgressBar { IsIndeterminate = true, Height = 7, BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.FindResource("BorderBrush") };
        progress.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        panel.Children.Add(progress);
        var cancel = new Button { Content = VehicleNameUpdateWindow.T("取消下载", "Cancel download"), HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 14, 0, 0) };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(cancel);
        Content = panel;
        Closed += (_, _) => cancellation.Cancel();
    }
}
