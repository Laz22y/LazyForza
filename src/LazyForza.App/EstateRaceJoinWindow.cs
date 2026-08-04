using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

internal sealed class EstateRaceJoinWindow : Window
{
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    private static readonly (string Name, string Value)[] Palette =
    [
        ("青蓝", "#42D7E8"),
        ("电光蓝", "#5A8CFF"),
        ("紫罗兰", "#B86CFF"),
        ("玫红", "#EE4FA6"),
        ("竞速红", "#FF4057"),
        ("暖橙", "#FF8A3D"),
        ("亮黄", "#FFD328"),
        ("荧光绿", "#B8F34A")
    ];

    private readonly TextBox serverAddress;
    private readonly TextBox displayName;
    private readonly TextBox teamName;
    private readonly PasswordBox password;
    private readonly TextBlock error;
    private readonly TextBlock roomInfo;
    private readonly StackPanel teamField;
    private readonly Func<string, CancellationToken, Task<EstateRaceServerDescriptor>> descriptorReader;
    private readonly Dictionary<string, Button> swatches = new(StringComparer.OrdinalIgnoreCase);
    private string selectedColor;
    private EstateRaceServerDescriptor? descriptor;
    private string? descriptorAddress;

    public EstateRaceJoinWindow(
        EstateRaceConnectionProfile saved,
        Func<string, CancellationToken, Task<EstateRaceServerDescriptor>> descriptorReader)
    {
        this.descriptorReader = descriptorReader;
        Title = "进入地产赛事房间";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("WindowBrush", Color.FromRgb(10, 14, 19));
        Foreground = ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248));
        FontFamily = UiFont;
        selectedColor = Palette.Any(item => item.Value.Equals(saved.ThemeColor, StringComparison.OrdinalIgnoreCase))
            ? saved.ThemeColor.ToUpperInvariant()
            : Palette[0].Value;

        serverAddress = Input(saved.ServerAddress);
        displayName = Input(saved.DisplayName);
        teamName = Input(saved.TeamName ?? string.Empty);
        password = new PasswordBox
        {
            MinHeight = 42,
            Padding = new Thickness(11, 8, 11, 8),
            Background = ResourceBrush("InputBrush", Color.FromRgb(10, 14, 20)),
            Foreground = Foreground,
            BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72))
        };
        error = Text(string.Empty, 12, FontWeights.Normal,
            ResourceBrush("DangerBrush", Color.FromRgb(255, 100, 118)));
        roomInfo = Text("正在读取房间设置…", 12, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)), new Thickness(0, 8, 0, 0));

        var root = new StackPanel { Margin = new Thickness(30, 24, 30, 26) };
        root.Children.Add(Text("进入房间", 28, FontWeights.SemiBold));
        root.Children.Add(Text(
            "先在“赛道”页面选中本场使用的地产环道并开始计时，再填写房间信息。比赛密码不会保存到本地。",
            13, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 6, 0, 20)));

        root.Children.Add(Field("服务端域名或 IP", "公网房间建议使用 https:// 域名；可信局域网可以填写主机 IP 和端口。修改地址后，进入房间时会重新读取服务端设置。", serverAddress));
        root.Children.Add(roomInfo);

        var identity = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameField = Field("比赛显示名", "2–20 个字符，本场不能与其他车手重名。", displayName);
        teamField = Field("车队名（可留空）", "只用于排行榜上的视觉分组，不影响比赛成绩。", teamName);
        teamField.Visibility = Visibility.Collapsed;
        Grid.SetColumn(teamField, 2);
        identity.Children.Add(nameField);
        identity.Children.Add(teamField);
        root.Children.Add(identity);

        var colorPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        colorPanel.Children.Add(Text("代表色", 13, FontWeights.SemiBold));
        colorPanel.Children.Add(Text(
            "代表色用于排行榜、赛道一览和车手标记。",
            12, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 3, 0, 8)));
        var palette = new WrapPanel();
        foreach (var item in Palette)
        {
            var swatch = new Button
            {
                Width = 52,
                Height = 38,
                Margin = new Thickness(0, 0, 9, 8),
                Padding = new Thickness(0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Value)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                ToolTip = $"{item.Name} · {item.Value}",
                Tag = item.Value
            };
            swatch.Click += (_, _) => SelectColor((string)swatch.Tag);
            swatches[item.Value] = swatch;
            palette.Children.Add(swatch);
        }
        colorPanel.Children.Add(palette);
        root.Children.Add(colorPanel);
        root.Children.Add(Field("赛事密码", "由房间管理员提供。", password));
        error.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(error);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "取消", MinWidth = 92, Padding = new Thickness(16, 8, 16, 8) };
        cancel.Click += (_, _) => DialogResult = false;
        var join = new Button
        {
            Content = "进入房间",
            MinWidth = 116,
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(18, 8, 18, 8),
            IsDefault = true,
            FontWeight = FontWeights.SemiBold
        };
        join.Click += async (_, _) => await AcceptAsync(join);
        actions.Children.Add(cancel);
        actions.Children.Add(join);
        root.Children.Add(actions);
        Content = root;
        SelectColor(selectedColor);
        Loaded += async (_, _) => await RefreshDescriptorAsync(showError: false);
    }

    public EstateRaceConnectionProfile? Profile { get; private set; }

    private async Task AcceptAsync(Button join)
    {
        error.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(serverAddress.Text))
        {
            error.Text = "请填写服务端域名或 IP。";
            serverAddress.Focus();
            return;
        }
        if (displayName.Text.Trim().Length is < 2 or > 20)
        {
            error.Text = "比赛显示名需要 2–20 个字符。";
            displayName.Focus();
            return;
        }
        if (teamName.Text.Trim().Length > 24)
        {
            error.Text = "车队名不能超过 24 个字符。";
            teamName.Focus();
            return;
        }
        if (password.Password.Length > 128) { error.Text = "赛事密码不能超过 128 个字符。"; return; }

        join.IsEnabled = false;
        var addressAlreadyLoaded = string.Equals(
            descriptorAddress,
            serverAddress.Text.Trim(),
            StringComparison.OrdinalIgnoreCase);
        if (!await RefreshDescriptorAsync(showError: true))
        {
            join.IsEnabled = true;
            return;
        }
        if (!addressAlreadyLoaded && descriptor?.AllowTeams == true)
        {
            error.Text = "该房间允许车队。请确认车队名（可留空），然后再次点击“进入房间”。";
            join.IsEnabled = true;
            teamName.Focus();
            return;
        }

        Profile = new EstateRaceConnectionProfile(
            serverAddress.Text.Trim(),
            password.Password,
            displayName.Text.Trim(),
            selectedColor,
            descriptor?.AllowTeams == false || string.IsNullOrWhiteSpace(teamName.Text)
                ? null
                : teamName.Text.Trim());
        DialogResult = true;
    }

    private async Task<bool> RefreshDescriptorAsync(bool showError)
    {
        if (string.IsNullOrWhiteSpace(serverAddress.Text)) return false;
        try
        {
            descriptor = await descriptorReader(serverAddress.Text.Trim(), CancellationToken.None);
            descriptorAddress = serverAddress.Text.Trim();
            teamField.Visibility = descriptor.AllowTeams ? Visibility.Visible : Visibility.Collapsed;
            roomInfo.Text = descriptor.ActiveTrackId is null
                ? $"{descriptor.ServerName} · 服务端尚未指定赛道 · {(descriptor.AllowTeams ? "允许车队" : "个人参赛")}"
                : $"{descriptor.ServerName} · {descriptor.ActiveTrackName ?? descriptor.ActiveTrackId} · {(descriptor.AllowTeams ? "允许车队" : "个人参赛")}";
            return true;
        }
        catch (Exception exception)
        {
            descriptor = null;
            descriptorAddress = null;
            roomInfo.Text = "暂时无法读取房间设置。";
            if (showError) error.Text = $"无法读取房间设置：{exception.Message}";
            return false;
        }
    }

    private void SelectColor(string value)
    {
        selectedColor = value;
        foreach (var pair in swatches)
        {
            pair.Value.BorderThickness = new Thickness(
                pair.Key.Equals(value, StringComparison.OrdinalIgnoreCase) ? 4 : 1);
            pair.Value.Opacity = pair.Key.Equals(value, StringComparison.OrdinalIgnoreCase) ? 1 : .72;
        }
    }

    private static StackPanel Field(string title, string detail, Control input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(Text(title, 13, FontWeights.SemiBold));
        panel.Children.Add(Text(
            detail, 12, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 3, 0, 6)));
        panel.Children.Add(input);
        return panel;
    }

    private static TextBox Input(string value) => new()
    {
        Text = value,
        MinHeight = 42,
        Padding = new Thickness(11, 8, 11, 8),
        Background = ResourceBrush("InputBrush", Color.FromRgb(10, 14, 20)),
        Foreground = ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248)),
        BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72))
    };

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        Brush? foreground = null,
        Thickness? margin = null) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground ?? ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248)),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size + 8,
        Margin = margin ?? new Thickness(0)
    };

    private static Brush ResourceBrush(string key, Color fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
