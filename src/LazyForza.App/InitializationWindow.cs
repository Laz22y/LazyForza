using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LazyForza.Domain;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace LazyForza.App;

internal sealed record InitializationResult(
    StartupProfile Profile,
    string PlayerCode,
    string ListenAddress,
    int Port);

internal sealed class InitializationWindow : Window
{
    private const int FinalStep = 5;
    private readonly StartupProfile sourceProfile;
    private readonly string? fixedDataDirectory;
    private readonly ContentControl pageHost = new();
    private readonly StackPanel stepRail = new();
    private readonly Button backButton = new();
    private readonly Button nextButton = new();
    private readonly Button closeButton = new();
    private readonly DispatcherTimer welcomeTimer;
    private CancellationTokenSource? probeCancellation;
    private Task? probeTask;
    private TextBlock? telemetryStatus;
    private TextBlock? telemetryDetail;
    private Border? telemetryStatusIcon;
    private Button? retryButton;
    private TextBlock? welcomeTitle;
    private int step;
    private int welcomeFrame;
    private bool completing;
    private bool captureMode;
    private string language;
    private string playerCode = string.Empty;
    private string dataDirectory;
    private MainWindowCloseBehavior closeBehavior;
    private string listenAddress = LazyForzaDefaults.TelemetryListenAddress;
    private string portText = LazyForzaDefaults.TelemetryPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public InitializationWindow(StartupProfile profile, string? explicitDataDirectory = null)
    {
        sourceProfile = profile.Normalize();
        fixedDataDirectory = string.IsNullOrWhiteSpace(explicitDataDirectory)
            ? null
            : Path.GetFullPath(explicitDataDirectory);
        language = sourceProfile.Language;
        dataDirectory = fixedDataDirectory ?? sourceProfile.DataDirectory;
        closeBehavior = sourceProfile.CloseBehavior;
        AppLocalization.UseLanguage(language);

        Title = "LazyForza Setup";
        Width = 1060;
        Height = 620;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LazyForza.png", UriKind.Absolute));
        Content = BuildWindow();
        welcomeTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.3), DispatcherPriority.Normal, (_, _) =>
        {
            if (step != 0) return;
            welcomeFrame++;
            RefreshWelcomeTitle();
        }, Dispatcher);
        welcomeTimer.Start();
        Closing += OnClosing;
        Loaded += (_, _) => Render();
    }

    public InitializationResult? Result { get; private set; }

    internal async Task CaptureQaAsync(string directory)
    {
        captureMode = true;
        Directory.CreateDirectory(directory);
        AppLocalization.UseLanguage("zh-Hans");
        language = "zh-Hans";
        for (var index = 0; index <= FinalStep; index++)
        {
            step = index;
            welcomeFrame = 0;
            Render();
            await Task.Delay(260);
            CapturePng(Path.Combine(directory, $"initialization-{index + 1}.png"));
            if (index == 0)
            {
                welcomeFrame = 1;
                RefreshWelcomeTitle();
                await Task.Delay(260);
                CapturePng(Path.Combine(directory, "initialization-1-en-welcome.png"));
            }
        }

        AppLocalization.UseLanguage("en");
        language = "en";
        for (var index = 0; index <= FinalStep; index++)
        {
            step = index;
            welcomeFrame = 1;
            Render();
            await Task.Delay(260);
            CapturePng(Path.Combine(directory, $"initialization-{index + 1}-en.png"));
        }
    }

    private UIElement BuildWindow()
    {
        var chrome = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(9, 14, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 66, 82)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            ClipToBounds = true
        };
        chrome.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 30,
            ShadowDepth = 0,
            Opacity = 0.48,
            Color = Colors.Black
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        root.Children.Add(AmbientBackground());

        var header = new Grid { Margin = new Thickness(24, 0, 18, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new Image
        {
            Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LazyForzaWordmark.png", UriKind.Absolute)),
            Width = 146,
            Height = 32,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(brand);
        closeButton.Content = "×";
        closeButton.Width = 34;
        closeButton.Height = 34;
        closeButton.Padding = new Thickness(0);
        closeButton.FontSize = 20;
        closeButton.Background = Brushes.Transparent;
        closeButton.BorderBrush = Brushes.Transparent;
        closeButton.ToolTip = AppLocalization.Text("wizard.exit", "退出初始化");
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);
        header.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var divider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(110, 38, 53, 67)),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(divider, 0);
        root.Children.Add(divider);

        var body = new Grid { Margin = new Thickness(22, 12, 22, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepRail.Margin = new Thickness(6, 20, 24, 12);
        body.Children.Add(stepRail);
        var railDivider = new Border { Background = new SolidColorBrush(Color.FromRgb(35, 48, 61)) };
        Grid.SetColumn(railDivider, 1);
        body.Children.Add(railDivider);
        pageHost.Margin = new Thickness(42, 18, 20, 12);
        Grid.SetColumn(pageHost, 2);
        body.Children.Add(pageHost);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(28, 10, 28, 14) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backButton.MinWidth = 100;
        backButton.Padding = new Thickness(18, 9, 18, 9);
        backButton.Click += (_, _) => Move(-1);
        Grid.SetColumn(backButton, 1);
        footer.Children.Add(backButton);
        nextButton.MinWidth = 128;
        nextButton.Padding = new Thickness(20, 9, 20, 9);
        nextButton.Background = new SolidColorBrush(Color.FromRgb(16, 126, 148));
        nextButton.BorderBrush = new SolidColorBrush(Color.FromRgb(39, 198, 221));
        nextButton.Click += async (_, _) =>
        {
            if (step == FinalStep)
                await SkipTelemetryAsync();
            else
                Move(1);
        };
        Grid.SetColumn(nextButton, 2);
        footer.Children.Add(nextButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        chrome.Child = root;
        return chrome;
    }

    private UIElement AmbientBackground()
    {
        var canvas = new Canvas { IsHitTestVisible = false, Opacity = 0.42 };
        var cyan = new Ellipse
        {
            Width = 430,
            Height = 430,
            Fill = new RadialGradientBrush(
                Color.FromArgb(70, 19, 185, 210),
                Color.FromArgb(0, 9, 14, 20)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.9, 0.9)
        };
        Canvas.SetLeft(cyan, 700);
        Canvas.SetTop(cyan, -170);
        canvas.Children.Add(cyan);
        var pulse = new DoubleAnimation(0.82, 1.08, TimeSpan.FromSeconds(4.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ((ScaleTransform)cyan.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        ((ScaleTransform)cyan.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        var purple = new Ellipse
        {
            Width = 360,
            Height = 360,
            Fill = new RadialGradientBrush(
                Color.FromArgb(45, 166, 72, 225),
                Color.FromArgb(0, 9, 14, 20))
        };
        Canvas.SetLeft(purple, -140);
        Canvas.SetTop(purple, 430);
        canvas.Children.Add(purple);
        return canvas;
    }

    private void Render(bool textOnlyLanguageRefresh = false)
    {
        RenderRail();
        RenderPage(textOnlyLanguageRefresh);
        backButton.Content = AppLocalization.Text("wizard.back", "上一步");
        backButton.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        backButton.IsEnabled = !completing;
        nextButton.Content = step switch
        {
            0 => AppLocalization.Text("wizard.start", "开始设置"),
            FinalStep => AppLocalization.Text("wizard.telemetry.skip", "跳过连接并进入"),
            _ => AppLocalization.Text("wizard.next", "下一步")
        };
        nextButton.Visibility = Visibility.Visible;
        nextButton.IsEnabled = !completing;
        closeButton.ToolTip = AppLocalization.Text("wizard.exit", "退出初始化");
        if (textOnlyLanguageRefresh)
            Dispatcher.BeginInvoke(AnimateLanguageTextRefresh, DispatcherPriority.Loaded);
    }

    private void RenderRail()
    {
        stepRail.Children.Clear();
        var names = new[]
        {
            AppLocalization.Text("wizard.step.welcome", "欢迎"),
            AppLocalization.Text("wizard.step.language", "语言"),
            AppLocalization.Text("wizard.step.identity", "玩家代号"),
            AppLocalization.Text("wizard.step.storage", "数据存储"),
            AppLocalization.Text("wizard.step.close", "关闭方式"),
            AppLocalization.Text("wizard.step.telemetry", "连接 FH6")
        };
        for (var index = 0; index < names.Length; index++)
        {
            var active = index == step;
            var completed = index < step;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 17) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(24, 174, 197))
                    : completed
                        ? new SolidColorBrush(Color.FromRgb(28, 89, 101))
                        : new SolidColorBrush(Color.FromRgb(23, 31, 42)),
                BorderBrush = active
                    ? new SolidColorBrush(Color.FromRgb(92, 225, 239))
                    : new SolidColorBrush(Color.FromRgb(48, 62, 77)),
                BorderThickness = new Thickness(1),
                Child = Text(completed ? "✓" : (index + 1).ToString(), 11, FontWeights.Bold)
            };
            ((TextBlock)marker.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)marker.Child).VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(marker);
            var name = Text(names[index], 13, active ? FontWeights.SemiBold : FontWeights.Normal,
                active ? "TextBrush" : "MutedBrush");
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            stepRail.Children.Add(row);
        }
    }

    private void RenderPage(bool textOnlyLanguageRefresh = false)
    {
        welcomeTitle = null;
        var page = step switch
        {
            0 => WelcomePage(),
            1 => LanguagePage(),
            2 => IdentityPage(),
            3 => StoragePage(),
            4 => CloseBehaviorPage(),
            _ => TelemetryPage()
        };
        pageHost.Content = page;
        if (textOnlyLanguageRefresh) return;
        page.Opacity = 0;
        page.RenderTransform = new TranslateTransform(16, 0);
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        ((TranslateTransform)page.RenderTransform).BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(330))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private FrameworkElement WelcomePage()
    {
        var panel = new Grid();
        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var chinese = welcomeFrame % 2 == 0;
        welcomeTitle = Text(chinese ? "欢迎使用" : "WELCOME TO", chinese ? 44 : 38, FontWeights.Bold);
        content.Children.Add(welcomeTitle);
        var product = Text("LazyForza", 58, FontWeights.Bold);
        product.Foreground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(37, 215, 242), 0),
                new GradientStop(Color.FromRgb(65, 126, 255), 0.53),
                new GradientStop(Color.FromRgb(181, 66, 255), 1)
            }
        };
        product.Margin = new Thickness(0, 2, 0, 0);
        content.Children.Add(product);
        panel.Children.Add(content);
        return panel;
    }

    private FrameworkElement LanguagePage()
    {
        var stack = Page(
            AppLocalization.Text("wizard.language.title", "选择语言"),
            AppLocalization.Text("wizard.language.description", "界面语言可随时在设置页修改。"));
        var choices = new Grid { Margin = new Thickness(0, 25, 0, 0) };
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < AppLocalization.SupportedLanguages.Count; index++)
        {
            var option = AppLocalization.SupportedLanguages[index];
            var card = ChoiceCard(
                option.NativeName,
                option.Code == "zh-Hans" ? "Chinese · 简体" : "English · EN",
                option.Code.Equals(language, StringComparison.OrdinalIgnoreCase),
                () =>
                {
                    language = option.Code;
                    AppLocalization.UseLanguage(language);
                    Render(textOnlyLanguageRefresh: true);
                });
            card.Margin = new Thickness(index == 0 ? 0 : 8, 0, index == 0 ? 8 : 0, 0);
            Grid.SetColumn(card, index);
            choices.Children.Add(card);
        }
        stack.Children.Add(choices);
        return stack;
    }

    private FrameworkElement IdentityPage()
    {
        var stack = Page(
            AppLocalization.Text("wizard.identity.title", "设置玩家代号"),
            AppLocalization.Text("wizard.identity.description", "用于新录制、圈速分享和首次加入地产赛事时的默认代表名。"));
        var card = new StackPanel();
        card.Children.Add(Text(AppLocalization.Text("wizard.identity.label", "玩家代号"), 12, FontWeights.SemiBold));
        var input = new TextBox
        {
            Text = playerCode,
            MaxLength = PlayerIdentitySettings.MaximumLength,
            Margin = new Thickness(0, 9, 0, 0),
            Padding = new Thickness(13, 10, 13, 10),
            FontSize = 17
        };
        input.TextChanged += (_, _) => playerCode = PlayerIdentitySettings.Normalize(input.Text);
        card.Children.Add(input);
        var hint = Text(
            AppLocalization.Text("wizard.identity.hint", "允许留空；最多 20 个字符，稍后可在设置页修改。"),
            11,
            FontWeights.Normal,
            "MutedBrush");
        hint.Margin = new Thickness(0, 9, 0, 0);
        card.Children.Add(hint);
        var identityCard = PanelCard(card);
        identityCard.Width = 700;
        identityCard.MaxWidth = 700;
        identityCard.HorizontalAlignment = HorizontalAlignment.Left;
        identityCard.Margin = new Thickness(0, 22, 0, 0);
        stack.Children.Add(identityCard);
        Dispatcher.BeginInvoke(() => input.Focus(), DispatcherPriority.Input);
        return stack;
    }

    private FrameworkElement StoragePage()
    {
        var stack = Page(
            AppLocalization.Text("wizard.storage.title", "选择数据存储目录"),
            fixedDataDirectory is null
                ? AppLocalization.Text("wizard.storage.description", "圈速、设置、录制与自定义赛道统一保存在这里。")
                : AppLocalization.Text("wizard.storage.fixed", "启动参数已指定数据目录，本次初始化将沿用该目录。"));
        var options = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        options.Children.Add(StorageChoice(
            AppLocalization.Text("wizard.storage.standard", "LazyForza（推荐）"),
            StartupProfileStore.DefaultDataDirectory));
        options.Children.Add(StorageChoice("LazyForza-Release", StartupProfileStore.ReleaseDataDirectory));
        options.Children.Add(StorageChoice(
            AppLocalization.Text("wizard.storage.program", "程序目录 · Data"),
            StartupProfileStore.ProgramDataDirectory));
        var custom = StorageChoice(
            AppLocalization.Text("wizard.storage.custom", "自定义文件夹"),
            IsKnownDataDirectory(dataDirectory) ? string.Empty : dataDirectory,
            custom: true);
        options.Children.Add(custom);
        stack.Children.Add(options);
        if (fixedDataDirectory is not null)
        {
            foreach (var child in options.Children.OfType<UIElement>()) child.IsEnabled = false;
        }
        return stack;
    }

    private Border StorageChoice(string title, string path, bool custom = false)
    {
        var choicePath = path;
        var selected = custom
            ? IsCustomSelection()
            : PathsEqual(choicePath, dataDirectory);
        var description = Text(string.IsNullOrWhiteSpace(choicePath)
            ? AppLocalization.Text("wizard.storage.choose", "选择一个文件夹")
            : choicePath, 10, FontWeights.Normal, "MutedBrush");
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.TextWrapping = TextWrapping.NoWrap;
        var card = ChoiceCard(title, description.Text, selected, () =>
        {
            if (fixedDataDirectory is not null) return;
            if (!custom)
            {
                dataDirectory = path;
                RenderPage();
                return;
            }
            var dialog = new OpenFolderDialog
            {
                Title = AppLocalization.Text("wizard.storage.dialog", "选择 LazyForza 数据目录"),
                InitialDirectory = Directory.Exists(dataDirectory)
                    ? dataDirectory
                    : StartupProfileStore.DefaultDataDirectory,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true)
            {
                dataDirectory = Path.GetFullPath(dialog.FolderName);
                RenderPage();
            }
        });
        card.Margin = new Thickness(0, 0, 0, 9);
        return card;
    }

    private FrameworkElement CloseBehaviorPage()
    {
        var stack = Page(
            AppLocalization.Text("wizard.close.title", "选择关闭窗口的行为"),
            AppLocalization.Text("wizard.close.description", "该选项只影响主窗口右上角的关闭按钮。"));
        var options = new StackPanel { Margin = new Thickness(0, 25, 0, 0) };
        options.Children.Add(ChoiceCard(
            AppLocalization.Text("wizard.close.tray", "最小化到托盘"),
            AppLocalization.Text("wizard.close.trayDetail", "HUD 与数据接收继续运行，可从托盘重新打开。"),
            closeBehavior == MainWindowCloseBehavior.MinimizeToTray,
            () =>
            {
                closeBehavior = MainWindowCloseBehavior.MinimizeToTray;
                RenderPage();
            }));
        var exit = ChoiceCard(
            AppLocalization.Text("wizard.close.exit", "关闭程序"),
            AppLocalization.Text("wizard.close.exitDetail", "停止遥测、HUD 与后台录制并退出 LazyForza。"),
            closeBehavior == MainWindowCloseBehavior.ExitApplication,
            () =>
            {
                closeBehavior = MainWindowCloseBehavior.ExitApplication;
                RenderPage();
            });
        exit.Margin = new Thickness(0, 12, 0, 0);
        options.Children.Add(exit);
        stack.Children.Add(options);
        return stack;
    }

    private FrameworkElement TelemetryPage()
    {
        var stack = Page(
            AppLocalization.Text("wizard.telemetry.title", "连接 FH6 遥测"),
            AppLocalization.Text("wizard.telemetry.description", "在游戏中启用 Data Out，LazyForza 收到有效数据后会自动完成初始化。"));
        var content = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var settings = new StackPanel();
        settings.Children.Add(Text(
            AppLocalization.Text("wizard.telemetry.endpointTitle", "监听端点"),
            13,
            FontWeights.SemiBold));
        var endpointSpacer = new Border { Height = 14 };
        settings.Children.Add(endpointSpacer);
        settings.Children.Add(InputRow(AppLocalization.Text("wizard.telemetry.address", "监听 IP"), listenAddress, value => listenAddress = value));
        settings.Children.Add(InputRow(AppLocalization.Text("wizard.telemetry.port", "UDP 端口"), portText, value => portText = value));
        var endpointCard = PanelCard(settings);
        endpointCard.Margin = new Thickness(0, 0, 9, 0);
        content.Children.Add(endpointCard);

        var connection = new StackPanel();
        connection.Children.Add(Text(
            AppLocalization.Text("wizard.telemetry.gameSettingsTitle", "游戏内设置"),
            13,
            FontWeights.SemiBold));
        var instructions = Text(
            AppLocalization.Text(
                "wizard.telemetry.instructions",
                "FH6 → 设置 → HUD 与游戏玩法 → Data Out\n开启 Data Out，并填写与左侧一致的 IP 和端口。"),
            12,
            FontWeights.Normal,
            "MutedBrush");
        instructions.LineHeight = 22;
        instructions.Margin = new Thickness(0, 12, 0, 0);
        connection.Children.Add(instructions);
        var statusRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        telemetryStatusIcon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 191, 211)),
            BorderThickness = new Thickness(2),
            Child = Text("•", 18, FontWeights.Bold, "AccentBrush")
        };
        ((TextBlock)telemetryStatusIcon.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)telemetryStatusIcon.Child).VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(telemetryStatusIcon);
        var statusCopy = new StackPanel();
        telemetryStatus = Text(AppLocalization.Text("wizard.telemetry.waiting", "正在等待遥测数据"), 13, FontWeights.SemiBold);
        telemetryDetail = Text(
            AppLocalization.Text("wizard.telemetry.waitingDetail", "启动游戏或进入驾驶画面后保持此窗口打开。"),
            10,
            FontWeights.Normal,
            "MutedBrush");
        telemetryDetail.Margin = new Thickness(0, 3, 0, 0);
        statusCopy.Children.Add(telemetryStatus);
        statusCopy.Children.Add(telemetryDetail);
        Grid.SetColumn(statusCopy, 1);
        statusRow.Children.Add(statusCopy);
        connection.Children.Add(statusRow);
        retryButton = new Button
        {
            Content = AppLocalization.Text("wizard.telemetry.retry", "重新监听"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(38, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        retryButton.Click += (_, _) => BeginProbe();
        connection.Children.Add(retryButton);
        var connectionCard = PanelCard(connection);
        connectionCard.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(connectionCard, 1);
        content.Children.Add(connectionCard);
        stack.Children.Add(content);
        if (!captureMode)
            Dispatcher.BeginInvoke(BeginProbe, DispatcherPriority.Background);
        return stack;
    }

    private void BeginProbe() => probeTask = StartProbeAsync();

    private FrameworkElement InputRow(string label, string value, Action<string> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = Text(label, 11, FontWeights.SemiBold, "MutedBrush");
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(caption);
        var input = new TextBox { Text = value, Padding = new Thickness(10, 8, 10, 8) };
        input.TextChanged += (_, _) => changed(input.Text);
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return row;
    }

    private async Task StartProbeAsync()
    {
        if (step != FinalStep || completing || telemetryStatus is null || telemetryDetail is null) return;
        probeCancellation?.Cancel();
        probeCancellation?.Dispose();
        probeCancellation = new CancellationTokenSource();
        retryButton!.Visibility = Visibility.Collapsed;
        if (!TryReadTelemetryEndpoint(out var port))
        {
            ShowProbeError(
                AppLocalization.Text("wizard.telemetry.invalid", "IP 或端口无效"),
                AppLocalization.Text("wizard.telemetry.invalidDetail", "请输入有效 IP 和 1–65535 端口，并避开 5200–5300。"));
            return;
        }

        listenAddress = listenAddress.Trim();
        telemetryStatus.Text = AppLocalization.Text("wizard.telemetry.waiting", "正在等待遥测数据");
        telemetryDetail.Text = AppLocalization.Format(
            "wizard.telemetry.endpoint",
            "正在监听 {0}:{1}",
            listenAddress,
            port);
        telemetryStatus.Foreground = ResourceBrush("TextBrush");
        telemetryStatusIcon!.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 191, 211));
        telemetryStatusIcon.Child = Text("•", 18, FontWeights.Bold, "AccentBrush");
        ((TextBlock)telemetryStatusIcon.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)telemetryStatusIcon.Child).VerticalAlignment = VerticalAlignment.Center;
        try
        {
            var result = await new TelemetryInitializationProbe().WaitForTelemetryAsync(
                listenAddress,
                port,
                probeCancellation.Token);
            if (step != FinalStep) return;
            completing = true;
            backButton.IsEnabled = false;
            telemetryStatus.Text = AppLocalization.Text("wizard.telemetry.complete", "初始化完成");
            telemetryDetail.Text = AppLocalization.Format(
                "wizard.telemetry.completeDetail",
                "已接收 FH6 遥测 · {0} km/h · 正在启动 LazyForza",
                result.Frame.Normalized.SpeedKph.ToString("0"));
            telemetryStatus.Foreground = ResourceBrush("SuccessBrush");
            telemetryStatusIcon.Background = new SolidColorBrush(Color.FromRgb(35, 169, 107));
            telemetryStatusIcon.BorderBrush = new SolidColorBrush(Color.FromRgb(90, 232, 161));
            telemetryStatusIcon.Child = Text("✓", 15, FontWeights.Bold);
            ((TextBlock)telemetryStatusIcon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)telemetryStatusIcon.Child).VerticalAlignment = VerticalAlignment.Center;
            telemetryStatusIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(440))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2)
            });
            await Task.Delay(1250);
            CompleteInitialization(port);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            ShowProbeError(
                AppLocalization.Text("wizard.telemetry.failed", "无法开始监听"),
                exception is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? AppLocalization.Text("wizard.telemetry.inUse", "该端口已被其他程序占用，请更换端口或关闭占用程序。")
                    : exception.Message);
        }
    }

    private async Task SkipTelemetryAsync()
    {
        if (step != FinalStep || completing) return;
        if (!TryReadTelemetryEndpoint(out var port))
        {
            listenAddress = LazyForzaDefaults.TelemetryListenAddress;
            port = LazyForzaDefaults.TelemetryPort;
        }

        probeCancellation?.Cancel();
        if (probeTask is not null)
        {
            try
            {
                await probeTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (completing) return;
        completing = true;
        backButton.IsEnabled = false;
        nextButton.IsEnabled = false;
        CompleteInitialization(port);
    }

    private bool TryReadTelemetryEndpoint(out int port)
    {
        port = 0;
        if (!IPAddress.TryParse(listenAddress.Trim(), out _) ||
            !int.TryParse(portText.Trim(), out port) ||
            port is < 1 or > 65535 || port is >= 5200 and <= 5300)
            return false;
        listenAddress = listenAddress.Trim();
        return true;
    }

    private void CompleteInitialization(int port)
    {
        var profile = sourceProfile with
        {
            SchemaVersion = StartupProfile.CurrentSchemaVersion,
            InitializationCompleted = true,
            Language = language,
            DataDirectory = Path.GetFullPath(dataDirectory),
            CloseBehavior = closeBehavior,
            InitializationCompletedAt = DateTimeOffset.UtcNow
        };
        Result = new InitializationResult(
            profile.Normalize(),
            PlayerIdentitySettings.Normalize(playerCode),
            listenAddress,
            port);
        DialogResult = true;
    }

    private void ShowProbeError(string title, string detail)
    {
        if (telemetryStatus is null || telemetryDetail is null || retryButton is null) return;
        telemetryStatus.Text = title;
        telemetryStatus.Foreground = ResourceBrush("WarningBrush");
        telemetryDetail.Text = detail;
        retryButton.Visibility = Visibility.Visible;
    }

    private void Move(int offset)
    {
        if (completing) return;
        var next = Math.Clamp(step + offset, 0, FinalStep);
        if (next == step) return;
        probeCancellation?.Cancel();
        step = next;
        Render();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        welcomeTimer.Stop();
        probeCancellation?.Cancel();
        probeCancellation?.Dispose();
    }

    private void RefreshWelcomeTitle()
    {
        if (step != 0 || welcomeTitle is null) return;
        var chinese = welcomeFrame % 2 == 0;
        welcomeTitle.Text = chinese ? "欢迎使用" : "WELCOME TO";
        welcomeTitle.FontSize = chinese ? 44 : 38;
        AnimateTextBlock(welcomeTitle, TimeSpan.Zero);
    }

    private void AnimateLanguageTextRefresh()
    {
        UpdateLayout();
        var blocks = Descendants<TextBlock>(this)
            .Where(block => block.IsVisible)
            .OrderBy(block => HorizontalPosition(block))
            .ToArray();
        for (var index = 0; index < blocks.Length; index++)
            AnimateTextBlock(blocks[index], TimeSpan.FromMilliseconds(Math.Min(index * 9, 120)));
    }

    private double HorizontalPosition(Visual visual)
    {
        try
        {
            return visual.TransformToAncestor(this).Transform(new Point()).X;
        }
        catch (InvalidOperationException)
        {
            return double.MaxValue;
        }
    }

    private static void AnimateTextBlock(TextBlock block, TimeSpan beginTime)
    {
        block.Opacity = 0;
        var transform = new TranslateTransform(-12, 0);
        block.RenderTransform = transform;
        block.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = beginTime,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = beginTime,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static StackPanel Page(string title, string description)
    {
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        stack.Children.Add(Text(title, 30, FontWeights.Bold));
        var subtitle = Text(description, 13, FontWeights.Normal, "MutedBrush");
        subtitle.Margin = new Thickness(0, 8, 0, 0);
        subtitle.MaxWidth = 650;
        subtitle.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(subtitle);
        return stack;
    }

    private static Border ChoiceCard(
        string title,
        string description,
        bool selected,
        Action select)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        button.Click += (_, _) => select();
        var content = new Grid { Margin = new Thickness(16, 13, 18, 13) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new Border
        {
            Width = 21,
            Height = 21,
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(53, 214, 230)
                : Color.FromRgb(78, 93, 109)),
            BorderThickness = new Thickness(selected ? 6 : 1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(marker);
        var copy = new StackPanel();
        copy.Children.Add(Text(title, 14, FontWeights.SemiBold));
        var detail = Text(description, 10, FontWeights.Normal, "MutedBrush");
        detail.Margin = new Thickness(0, 4, 0, 0);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.TextWrapping = TextWrapping.NoWrap;
        copy.Children.Add(detail);
        Grid.SetColumn(copy, 1);
        content.Children.Add(copy);
        button.Content = content;
        return new Border
        {
            Background = new SolidColorBrush(selected
                ? Color.FromRgb(18, 45, 54)
                : Color.FromRgb(20, 28, 38)),
            BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(46, 170, 188)
                : Color.FromRgb(43, 57, 71)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Child = button
        };
    }

    private static Border PanelCard(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(20, 28, 38)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(43, 57, 71)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 18, 0, 0),
        Child = child
    };

    private bool IsCustomSelection() =>
        !PathsEqual(dataDirectory, StartupProfileStore.DefaultDataDirectory) &&
        !PathsEqual(dataDirectory, StartupProfileStore.ReleaseDataDirectory) &&
        !PathsEqual(dataDirectory, StartupProfileStore.ProgramDataDirectory);

    private static bool IsKnownDataDirectory(string path) =>
        PathsEqual(path, StartupProfileStore.DefaultDataDirectory) ||
        PathsEqual(path, StartupProfileStore.ReleaseDataDirectory) ||
        PathsEqual(path, StartupProfileStore.ProgramDataDirectory);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static TextBlock Text(string value, double size, FontWeight weight, string? brush = null) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = ResourceBrush(brush ?? "TextBrush"),
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Microsoft YaHei UI")
    };

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private void CapturePng(string path)
    {
        InvalidateVisualTree(this);
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void InvalidateVisualTree(DependencyObject parent)
    {
        if (parent is UIElement element) element.InvalidateVisual();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            InvalidateVisualTree(VisualTreeHelper.GetChild(parent, index));
    }
}
