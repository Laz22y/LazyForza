using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
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
    private readonly ComboBox roleSelector;
    private readonly ComboBox teamSelector;
    private readonly PasswordBox password;
    private readonly TextBlock error;
    private readonly TextBlock roomInfo;
    private readonly StackPanel teamField;
    private readonly StackPanel colorPanel;
    private readonly Func<string, CancellationToken, Task<EstateRaceServerDescriptor>> descriptorReader;
    private readonly Dictionary<string, Button> swatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border colorBase;
    private readonly Ellipse colorIndicator;
    private readonly Rectangle hueIndicator;
    private readonly Grid hueStrip;
    private readonly TextBox hexColor;
    private readonly Border colorPreview;
    private readonly Border customColorPreview;
    private readonly Button customColorButton;
    private readonly Popup colorPickerPopup;
    private readonly string? savedTeamId;
    private readonly string? savedTeamName;
    private CancellationTokenSource? descriptorRefreshCancellation;
    private string selectedColor;
    private double selectedHue;
    private double selectedSaturation = 0.72;
    private double selectedValue = 0.91;
    private EstateRaceServerDescriptor? descriptor;

    public EstateRaceJoinWindow(
        EstateRaceConnectionProfile saved,
        Func<string, CancellationToken, Task<EstateRaceServerDescriptor>> descriptorReader)
    {
        this.descriptorReader = descriptorReader;
        savedTeamId = saved.TeamId;
        savedTeamName = saved.TeamName;
        Title = "进入地产赛事房间";
        Width = 720;
        Height = Math.Min(710, SystemParameters.WorkArea.Height * 0.90);
        MinHeight = Math.Min(600, SystemParameters.WorkArea.Height * 0.80);
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("WindowBrush", Color.FromRgb(10, 14, 19));
        Foreground = ResourceBrush("TextBrush", Color.FromRgb(244, 246, 248));
        FontFamily = UiFont;
        selectedColor = TryColor(saved.ThemeColor, out _) ? saved.ThemeColor.ToUpperInvariant() : Palette[0].Value;

        serverAddress = Input(saved.ServerAddress);
        serverAddress.TextChanged += (_, _) => ScheduleDescriptorRefresh(TimeSpan.FromSeconds(1));
        displayName = Input(saved.DisplayName);
        roleSelector = new ComboBox
        {
            MinHeight = 42,
            Padding = new Thickness(8, 5, 8, 5),
            Background = ResourceBrush("InputBrush", Color.FromRgb(10, 14, 20)),
            Foreground = Foreground,
            BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72)),
            ItemsSource = new[] { "参赛车手", "OB（转播）" },
            SelectedIndex = saved.IsObserver ? 1 : 0
        };
        teamSelector = new ComboBox
        {
            MinHeight = 42,
            Padding = new Thickness(8, 5, 8, 5),
            Background = ResourceBrush("InputBrush", Color.FromRgb(10, 14, 20)),
            Foreground = Foreground,
            BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72)),
            DisplayMemberPath = nameof(EstateRaceTeam.Name)
        };
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
            "参赛车手需要使用本场地产环道；OB 只接收赛事数据并显示 HUD，不上传遥测，也不计入参赛名额。比赛密码不会保存到本地。",
            13, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 6, 0, 20)));

        root.Children.Add(Field("服务端域名或 IP", "公网房间建议使用 https:// 域名；可信局域网可以填写主机 IP 和端口。进入房间前会读取服务端的赛道与车队设置。", serverAddress));
        root.Children.Add(roomInfo);
        root.Children.Add(Field(
            "连接身份",
            "参赛车手参与计时、排名和判罚；OB 仅用于观赛或转播，排行榜以榜首为比较基准。",
            roleSelector));

        var identity = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameField = Field("比赛显示名", "2–20 个字符，本场不能与其他车手或 OB 重名。", displayName);
        teamField = Field("参赛车队", "车队名单和每队人数由赛事总控设置。", teamSelector);
        teamField.Visibility = Visibility.Collapsed;
        Grid.SetColumn(teamField, 2);
        identity.Children.Add(nameField);
        identity.Children.Add(teamField);
        root.Children.Add(identity);

        colorPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        colorPanel.Children.Add(Text("代表色", 13, FontWeights.SemiBold));
        colorPanel.Children.Add(Text(
            "选择一个常用颜色，或点击末尾的彩虹色块自定义。代表色用于排行榜、赛道一览和车手标记。",
            12, FontWeights.Normal,
            ResourceBrush("MutedBrush", Color.FromRgb(157, 170, 185)),
            new Thickness(0, 3, 0, 8)));
        var palette = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
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

        var customButtonContent = new Grid { Width = 48, Height = 34 };
        customButtonContent.Children.Add(new TextBlock
        {
            Text = "+",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = .65,
                Color = Colors.Black
            }
        });
        customColorPreview = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 3, 3),
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = .55,
                Color = Colors.Black
            }
        };
        customButtonContent.Children.Add(customColorPreview);
        customColorButton = new Button
        {
            Width = 52,
            Height = 38,
            Margin = new Thickness(0, 0, 9, 8),
            Padding = new Thickness(0),
            Background = RainbowBrush(),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Content = customButtonContent,
            ToolTip = "自定义代表色",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        palette.Children.Add(customColorButton);
        colorPanel.Children.Add(palette);

        var customGrid = new Grid();
        customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        var surfaceControls = new StackPanel();
        var colorSurface = new Grid
        {
            Height = 132,
            ClipToBounds = true,
            Cursor = Cursors.Cross,
            Background = Brushes.Transparent
        };
        colorBase = new Border();
        colorSurface.Children.Add(colorBase);
        colorSurface.Children.Add(new Border
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(0, 0, 0, 0),
                Color.FromArgb(255, 0, 0, 0),
                new Point(0.5, 0),
                new Point(0.5, 1))
        });
        colorIndicator = new Ellipse
        {
            Width = 15,
            Height = 15,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        colorSurface.Children.Add(colorIndicator);
        colorSurface.MouseLeftButtonDown += (_, args) => SelectFromSurface(colorSurface, args.GetPosition(colorSurface));
        colorSurface.MouseMove += (_, args) =>
        {
            if (args.LeftButton == MouseButtonState.Pressed)
                SelectFromSurface(colorSurface, args.GetPosition(colorSurface));
        };
        surfaceControls.Children.Add(colorSurface);

        hueStrip = new Grid
        {
            Height = 20,
            Margin = new Thickness(0, 10, 0, 0),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Red, 0), new(Colors.Yellow, 1d / 6), new(Colors.Lime, 2d / 6),
                    new(Colors.Cyan, 3d / 6), new(Colors.Blue, 4d / 6), new(Colors.Magenta, 5d / 6),
                    new(Colors.Red, 1)
                },
                new Point(0, 0.5),
                new Point(1, 0.5))
        };
        hueIndicator = new Rectangle
        {
            Width = 4,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        hueStrip.Children.Add(hueIndicator);
        hueStrip.MouseLeftButtonDown += (_, args) => SelectHue(hueStrip, args.GetPosition(hueStrip).X);
        hueStrip.MouseMove += (_, args) =>
        {
            if (args.LeftButton == MouseButtonState.Pressed)
                SelectHue(hueStrip, args.GetPosition(hueStrip).X);
        };
        surfaceControls.Children.Add(hueStrip);
        customGrid.Children.Add(surfaceControls);

        var customControls = new StackPanel();
        Grid.SetColumn(customControls, 2);
        colorPreview = new Border
        {
            Height = 56,
            CornerRadius = new CornerRadius(5),
            BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72)),
            BorderThickness = new Thickness(1)
        };
        customControls.Children.Add(colorPreview);
        hexColor = Input(selectedColor);
        hexColor.Margin = new Thickness(0, 8, 0, 0);
        hexColor.MaxLength = 7;
        hexColor.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) ApplyHexColor();
        };
        customControls.Children.Add(hexColor);
        var applyColor = new Button
        {
            Content = "应用颜色",
            Margin = new Thickness(0, 7, 0, 0),
            MinHeight = 34
        };
        customControls.Children.Add(applyColor);
        customGrid.Children.Add(customControls);
        colorSurface.SizeChanged += (_, _) =>
        {
            if (TryColor(selectedColor, out var color)) RefreshColorControls(color);
        };
        hueStrip.SizeChanged += (_, _) =>
        {
            if (TryColor(selectedColor, out var color)) RefreshColorControls(color);
        };

        colorPickerPopup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = customColorButton,
            HorizontalOffset = -410,
            VerticalOffset = 4,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Width = 500,
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(8),
                Background = ResourceBrush("CardBrush", Color.FromRgb(18, 25, 34)),
                BorderBrush = ResourceBrush("BorderBrush", Color.FromRgb(47, 58, 72)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 6,
                    Opacity = .48,
                    Color = Colors.Black
                },
                Child = customGrid
            }
        };
        customColorButton.Click += (_, _) => colorPickerPopup.IsOpen = true;
        applyColor.Click += (_, _) =>
        {
            if (ApplyHexColor()) colorPickerPopup.IsOpen = false;
        };
        root.Children.Add(colorPanel);
        root.Children.Add(Field("赛事密码", "由房间管理员提供，可以为空。", password));
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
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        SelectColor(selectedColor);
        teamSelector.SelectionChanged += (_, _) =>
        {
            if (teamSelector.SelectedItem is EstateRaceTeam team)
                SelectColor(team.ThemeColor);
        };
        roleSelector.SelectionChanged += (_, _) => UpdateRoleFields();
        UpdateRoleFields();
        Loaded += (_, _) => ScheduleDescriptorRefresh(TimeSpan.Zero);
        Closed += (_, _) =>
        {
            colorPickerPopup.IsOpen = false;
            CancelDescriptorRefresh();
        };
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
        if (password.Password.Length > 128)
        {
            error.Text = "赛事密码不能超过 128 个字符。";
            return;
        }

        join.IsEnabled = false;
        try
        {
            CancelDescriptorRefresh();
            var refreshCancellation = new CancellationTokenSource();
            descriptorRefreshCancellation = refreshCancellation;
            if (!await RefreshDescriptorAfterDelayAsync(
                    serverAddress.Text.Trim(),
                    TimeSpan.Zero,
                    showError: true,
                    refreshCancellation))
                return;
            var isObserver = IsObserverSelected;
            if (isObserver && descriptor?.SupportsObservers != true)
            {
                error.Text = "该服务端版本不支持 OB 身份，请让房主更新服务端。";
                roleSelector.Focus();
                return;
            }
            var selectedTeam = !isObserver && descriptor?.AllowTeams == true
                ? teamSelector.SelectedItem as EstateRaceTeam
                : null;
            var legacyTeamName = !isObserver && descriptor?.AllowTeams == true && descriptor.Teams is not { Count: > 0 }
                ? teamSelector.Text.Trim()
                : null;
            if (!isObserver && descriptor?.AllowTeams == true && descriptor.Teams is { Count: > 0 } && selectedTeam is null)
            {
                error.Text = "请选择本场参赛的车队。";
                teamSelector.Focus();
                return;
            }

            Profile = new EstateRaceConnectionProfile(
                serverAddress.Text.Trim(),
                password.Password,
                displayName.Text.Trim(),
                selectedColor,
                selectedTeam?.Name ?? (string.IsNullOrWhiteSpace(legacyTeamName) ? null : legacyTeamName),
                selectedTeam?.Id,
                isObserver ? EstateRaceConnectionRole.Observer : EstateRaceConnectionRole.Driver);
            DialogResult = true;
        }
        finally
        {
            join.IsEnabled = true;
        }
    }

    private void ScheduleDescriptorRefresh(TimeSpan delay)
    {
        CancelDescriptorRefresh();
        descriptor = null;
        UpdateRoleFields();
        error.Text = string.Empty;
        var address = serverAddress.Text.Trim();
        if (address.Length == 0)
        {
            roomInfo.Text = "填写服务端地址后会自动读取房间设置。";
            return;
        }

        roomInfo.Text = delay > TimeSpan.Zero
            ? "地址已更改，输入停止后将自动读取房间设置…"
            : "正在读取房间设置…";
        var refreshCancellation = new CancellationTokenSource();
        descriptorRefreshCancellation = refreshCancellation;
        _ = RefreshDescriptorAfterDelayAsync(address, delay, showError: false, refreshCancellation);
    }

    private void CancelDescriptorRefresh()
    {
        descriptorRefreshCancellation?.Cancel();
        descriptorRefreshCancellation = null;
    }

    private async Task<bool> RefreshDescriptorAfterDelayAsync(
        string address,
        TimeSpan delay,
        bool showError,
        CancellationTokenSource refreshCancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, refreshCancellation.Token);
            return await RefreshDescriptorAsync(address, showError, refreshCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(descriptorRefreshCancellation, refreshCancellation))
                descriptorRefreshCancellation = null;
            refreshCancellation.Dispose();
        }
    }

    private async Task<bool> RefreshDescriptorAsync(
        string address,
        bool showError,
        CancellationToken cancellationToken)
    {
        if (address.Length == 0) return false;
        try
        {
            roomInfo.Text = "正在读取房间设置…";
            var previous = teamSelector.SelectedItem as EstateRaceTeam;
            var received = await descriptorReader(address, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(address, serverAddress.Text.Trim(), StringComparison.Ordinal)) return false;
            descriptor = received;
            UpdateRoleFields();
            var choices = descriptor.Teams?.Where(team =>
                    !string.IsNullOrWhiteSpace(team.Id) && !string.IsNullOrWhiteSpace(team.Name))
                .ToArray() ?? [];
            teamSelector.IsEditable = descriptor.AllowTeams && choices.Length == 0;
            teamSelector.ItemsSource = choices;
            if (descriptor.AllowTeams && choices.Length > 0)
            {
                teamSelector.SelectedItem = choices.FirstOrDefault(team =>
                    string.Equals(team.Id, previous?.Id ?? savedTeamId, StringComparison.OrdinalIgnoreCase)) ??
                    choices.FirstOrDefault(team =>
                        string.Equals(team.Name, previous?.Name ?? savedTeamName, StringComparison.OrdinalIgnoreCase)) ??
                    choices[0];
            }
            else if (descriptor.AllowTeams)
            {
                teamSelector.Text = previous?.Name ?? savedTeamName ?? string.Empty;
            }
            roomInfo.Text = descriptor.ActiveTrackId is null
                ? $"{descriptor.ServerName} · 服务端尚未指定赛道 · {RoomModeText(descriptor)}"
                : $"{descriptor.ServerName} · {descriptor.ActiveTrackName ?? descriptor.ActiveTrackId} · {RoomModeText(descriptor)}";
            return true;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (!string.Equals(address, serverAddress.Text.Trim(), StringComparison.Ordinal)) return false;
            descriptor = null;
            UpdateRoleFields();
            roomInfo.Text = "暂时无法读取房间设置。";
            if (showError) error.Text = $"无法读取房间设置：{exception.Message}";
            return false;
        }
    }

    private static string RoomModeText(EstateRaceServerDescriptor value)
    {
        var raceMode = value.AllowTeams
            ? value.Teams is { Count: > 0 }
                ? $"{value.Teams.Count} 支车队 · 每队最多 {value.DriversPerTeam} 人"
                : "允许车队（旧版服务端自由填写）"
            : "个人参赛";
        return value.SupportsObservers
            ? $"{raceMode} · 支持 OB 转播"
            : raceMode;
    }

    private bool IsObserverSelected => roleSelector.SelectedIndex == 1;

    private void UpdateRoleFields()
    {
        var observer = IsObserverSelected;
        teamField.Visibility = !observer && descriptor?.AllowTeams == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        colorPanel.Visibility = observer ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SelectFromSurface(FrameworkElement surface, Point point)
    {
        selectedSaturation = Math.Clamp(point.X / Math.Max(1, surface.ActualWidth), 0, 1);
        selectedValue = 1 - Math.Clamp(point.Y / Math.Max(1, surface.ActualHeight), 0, 1);
        UpdateSelectedColor();
    }

    private void SelectHue(FrameworkElement strip, double x)
    {
        selectedHue = 360 * Math.Clamp(x / Math.Max(1, strip.ActualWidth), 0, 1);
        UpdateSelectedColor();
    }

    private bool ApplyHexColor()
    {
        if (!TryColor(hexColor.Text, out _))
        {
            error.Text = "代表色需要填写为 #RRGGBB，例如 #42D7E8。";
            return false;
        }
        error.Text = string.Empty;
        SelectColor(hexColor.Text);
        return true;
    }

    private void SelectColor(string value)
    {
        if (!TryColor(value, out var color)) return;
        selectedColor = value.ToUpperInvariant();
        (selectedHue, selectedSaturation, selectedValue) = ToHsv(color);
        RefreshColorControls(color);
    }

    private void UpdateSelectedColor()
    {
        var color = FromHsv(selectedHue, selectedSaturation, selectedValue);
        selectedColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RefreshColorControls(color);
    }

    private void RefreshColorControls(Color color)
    {
        colorBase.Background = new LinearGradientBrush(
            Colors.White,
            FromHsv(selectedHue, 1, 1),
            new Point(0, 0.5),
            new Point(1, 0.5));
        colorPreview.Background = new SolidColorBrush(color);
        customColorPreview.Background = new SolidColorBrush(color);
        customColorButton.ToolTip = $"自定义代表色 · {selectedColor}";
        hexColor.Text = selectedColor;
        var surfaceWidth = Math.Max(1, colorBase.ActualWidth);
        var surfaceHeight = Math.Max(1, colorBase.ActualHeight);
        colorIndicator.HorizontalAlignment = HorizontalAlignment.Left;
        colorIndicator.VerticalAlignment = VerticalAlignment.Top;
        colorIndicator.Margin = new Thickness(
            Math.Clamp(selectedSaturation * surfaceWidth - colorIndicator.Width / 2, -colorIndicator.Width / 2, surfaceWidth),
            Math.Clamp((1 - selectedValue) * surfaceHeight - colorIndicator.Height / 2, -colorIndicator.Height / 2, surfaceHeight),
            0,
            0);
        hueIndicator.Margin = new Thickness(Math.Max(0, selectedHue / 360 * Math.Max(1, hueStrip.ActualWidth) - 2), 0, 0, 0);
        foreach (var pair in swatches)
        {
            var active = pair.Key.Equals(selectedColor, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderThickness = new Thickness(active ? 4 : 1);
            pair.Value.Opacity = active ? 1 : .72;
        }
        var customActive = !swatches.ContainsKey(selectedColor);
        customColorButton.BorderThickness = new Thickness(customActive ? 4 : 1);
        customColorButton.Opacity = customActive ? 1 : .78;
    }

    private static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#' ||
            !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
            return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == red
            ? 60 * (((green - blue) / delta) % 6)
            : maximum == green
                ? 60 * ((blue - red) / delta + 2)
                : 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, maximum == 0 ? 0 : delta / maximum, maximum);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        var chroma = value * saturation;
        var part = hue / 60;
        var second = chroma * (1 - Math.Abs(part % 2 - 1));
        var (red, green, blue) = part switch
        {
            < 1 => (chroma, second, 0d),
            < 2 => (second, chroma, 0d),
            < 3 => (0d, chroma, second),
            < 4 => (0d, second, chroma),
            < 5 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };
        var match = value - chroma;
        return Color.FromRgb(
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }

    private static Brush RainbowBrush() => new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Colors.Red, 0),
            new(Colors.Yellow, .17),
            new(Colors.Lime, .34),
            new(Colors.Cyan, .51),
            new(Colors.Blue, .68),
            new(Colors.Magenta, .85),
            new(Colors.Red, 1)
        },
        new Point(0, 0.5),
        new Point(1, 0.5));

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
