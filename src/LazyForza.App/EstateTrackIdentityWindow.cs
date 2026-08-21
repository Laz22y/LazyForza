using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed class EstateTrackIdentityWindow : Window
{
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    private readonly EstateTrackPackageIdentity identity;
    private readonly TextBlock copyStatus;

    public EstateTrackIdentityWindow(EstateTrackPackageIdentity identity, string? exportedPath = null)
    {
        this.identity = identity;
        Title = "地产赛事赛道信息";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("WindowBrush", Color.FromRgb(10, 14, 19));
        Foreground = ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248));
        FontFamily = UiFont;

        var root = new StackPanel { Margin = new Thickness(30, 24, 30, 26) };
        root.Children.Add(Text(exportedPath is null ? "赛事赛道信息" : "地产环道已导出", 26, FontWeights.SemiBold));
        root.Children.Add(Text(
            exportedPath is null
                ? "下面的信息可用于核对赛道。房主也可以直接在服务端总控上传 .lfzestate，由服务端自动识别并填写。"
                : $"文件已保存到：{exportedPath}\n把这份 .lfzestate 上传到服务端总控即可，服务端会自动识别赛道名称、标识和特征值。",
            13,
            FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 6, 0, 18)));

        root.Children.Add(Field("赛道名称", identity.TrackName));
        root.Children.Add(Field("赛道标识", identity.TrackId.ToString("D")));
        root.Children.Add(Field("赛道特征 SHA-256", identity.TrackFingerprintSha256));
        root.Children.Add(Text(
            $"地图修订：{identity.MapRevision}    分段数：{identity.SectorCount}\n特征值只对应会影响比赛的赛道几何、分段、终点门、检查点和维修区；本地数据来源、名称、圈速记录及个人配置不会改变它。",
            12,
            FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 2, 0, 16)));

        copyStatus = Text(string.Empty, 12, FontWeights.Normal,
            ResourceBrush("AccentBrush", Color.FromRgb(32, 184, 207)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copyAll = new Button { Content = "复制全部", MinWidth = 108, Padding = new Thickness(16, 8, 16, 8) };
        copyAll.Click += (_, _) => Copy(AllText(), "已复制全部赛事赛道信息。");
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 92,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true
        };
        close.Click += (_, _) => Close();
        actions.Children.Add(copyAll);
        actions.Children.Add(close);
        root.Children.Add(copyStatus);
        root.Children.Add(actions);
        Content = root;
    }

    private Grid Field(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text(label, 13, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(title);
        var input = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            MinHeight = 40,
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        var copy = new Button
        {
            Content = "复制",
            MinWidth = 72,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(10, 0, 0, 0)
        };
        copy.Click += (_, _) => Copy(value, $"已复制{label}。");
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);
        return grid;
    }

    private string AllText() =>
        $"赛道名称：{identity.TrackName}\r\n" +
        $"赛道标识：{identity.TrackId:D}\r\n" +
        $"地图修订：{identity.MapRevision}\r\n" +
        $"赛道特征 SHA-256：{identity.TrackFingerprintSha256}";

    private void Copy(string value, string success)
    {
        try
        {
            Clipboard.SetText(value);
            copyStatus.Text = success;
        }
        catch (Exception exception)
        {
            copyStatus.Text = $"复制失败：{exception.Message}";
        }
    }

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        Brush? brush = null,
        Thickness? margin = null) => new()
        {
            Text = value,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush ?? ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248)),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0)
        };

    private static Brush ResourceBrush(string key, Color fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
