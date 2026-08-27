using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LazyForza.App;

internal static class AppDialog
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        ShowCore(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        ShowCore(owner, messageBoxText, caption, button, icon);

    public static MessageBoxResult ShowChoice(
        Window owner,
        string messageBoxText,
        string caption,
        string primaryText,
        string secondaryText,
        MessageBoxImage icon)
    {
        if (Application.Current is null)
            return System.Windows.MessageBox.Show(
                owner,
                messageBoxText,
                caption,
                MessageBoxButton.YesNo,
                icon);

        var dialog = new AppDialogWindow(
            messageBoxText,
            caption,
            icon,
            [
                new DialogButtonSpec(secondaryText, MessageBoxResult.No, false),
                new DialogButtonSpec(primaryText, MessageBoxResult.Yes, true)
            ],
            MessageBoxResult.No);
        AssignOwner(dialog, owner);
        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    private static MessageBoxResult ShowCore(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        if (Application.Current is null)
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);

        var (buttons, safeResult) = StandardButtons(button);
        var dialog = new AppDialogWindow(messageBoxText, caption, icon, buttons, safeResult);
        AssignOwner(dialog, owner);
        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    private static void AssignOwner(Window dialog, Window? requestedOwner)
    {
        var owner = requestedOwner;
        if (owner?.IsVisible != true)
            owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive && window.IsVisible) ??
                    Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(window => window.IsVisible);
        if (owner?.IsVisible == true && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    internal static (IReadOnlyList<DialogButtonSpec> Buttons, MessageBoxResult SafeResult)
        StandardButtons(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OKCancel =>
            ([
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.cancel", "取消"),
                    MessageBoxResult.Cancel,
                    false),
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.ok", "确定"),
                    MessageBoxResult.OK,
                    true)
            ], MessageBoxResult.Cancel),
            MessageBoxButton.YesNo =>
            ([
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.no", "否"),
                    MessageBoxResult.No,
                    false),
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.yes", "是"),
                    MessageBoxResult.Yes,
                    true)
            ], MessageBoxResult.No),
            MessageBoxButton.YesNoCancel =>
            ([
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.cancel", "取消"),
                    MessageBoxResult.Cancel,
                    false),
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.no", "否"),
                    MessageBoxResult.No,
                    false),
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.yes", "是"),
                    MessageBoxResult.Yes,
                    true)
            ], MessageBoxResult.Cancel),
            _ =>
            ([
                new DialogButtonSpec(
                    AppLocalization.Text("dialog.ok", "确定"),
                    MessageBoxResult.OK,
                    true)
            ], MessageBoxResult.OK)
        };
}

internal sealed class AppDialogWindow : Window
{
    private readonly MessageBoxResult safeResult;

    public AppDialogWindow(
        string message,
        string caption,
        MessageBoxImage icon,
        IReadOnlyList<DialogButtonSpec> buttons,
        MessageBoxResult safeResult)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(caption);
        ArgumentNullException.ThrowIfNull(buttons);
        this.safeResult = safeResult;
        Result = MessageBoxResult.None;
        Title = caption;
        Width = 520;
        MinWidth = 420;
        MaxWidth = 680;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            if (Result == MessageBoxResult.None) Result = this.safeResult;
        };

        var surface = new Border
        {
            Background = ResourceBrush("WindowBrush", Brushes.Black),
            BorderBrush = ResourceBrush("BorderBrush", Brushes.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            SnapsToDevicePixels = true
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.Child = root;

        var titleBar = new Grid
        {
            Background = Brushes.Transparent,
            Height = 48
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (eventArgs.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        var title = new TextBlock
        {
            Text = caption,
            Foreground = ResourceBrush("TextBrush", Brushes.White),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBar.Children.Add(title);
        var close = new Button
        {
            Content = "×",
            Width = 44,
            Height = 38,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = 20,
            ToolTip = AppLocalization.Text("dialog.close", "关闭")
        };
        close.Click += (_, _) => Complete(this.safeResult);
        Grid.SetColumn(close, 1);
        titleBar.Children.Add(close);
        root.Children.Add(new Border
        {
            Background = ResourceBrush("PanelBrush", Brushes.Black),
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            Child = titleBar
        });

        var body = new Grid { Margin = new Thickness(24, 24, 24, 18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (symbol, symbolBrush) = IconPresentation(icon);
        var iconSurface = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = symbolBrush,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = symbol,
                Foreground = ResourceBrush("WindowBrush", Brushes.Black),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        body.Children.Add(iconSurface);
        var messageBlock = new TextBlock
        {
            Text = message,
            Foreground = ResourceBrush("TextBrush", Brushes.White),
            FontSize = 13,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        };
        var messageScroll = new ScrollViewer
        {
            Content = messageBlock,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(messageScroll, 1);
        body.Children.Add(messageScroll);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Border
        {
            Background = ResourceBrush("PanelBrush", Brushes.Black),
            BorderBrush = ResourceBrush("BorderBrush", Brushes.DimGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 11, 11),
            Padding = new Thickness(18, 13, 18, 13)
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        foreach (var specification in buttons)
        {
            var action = new Button
            {
                Content = specification.Text,
                MinWidth = 92,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = specification.IsPrimary,
                IsCancel = specification.Result == safeResult
            };
            if (specification.IsPrimary)
            {
                action.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                action.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
                action.SetResourceReference(Control.ForegroundProperty, "WindowBrush");
            }
            action.Click += (_, _) => Complete(specification.Result);
            actions.Children.Add(action);
        }
        footer.Child = actions;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = surface;
    }

    public MessageBoxResult Result { get; private set; }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        eventArgs.Handled = true;
        Complete(safeResult);
    }

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        Close();
    }

    private static (string Symbol, Brush Brush) IconPresentation(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => ("×", ResourceBrush("DangerBrush", Brushes.IndianRed)),
        MessageBoxImage.Warning => ("!", ResourceBrush("WarningBrush", Brushes.Goldenrod)),
        MessageBoxImage.Question => ("?", ResourceBrush("AccentBrush", Brushes.DeepSkyBlue)),
        MessageBoxImage.Information => ("i", ResourceBrush("AccentBrush", Brushes.DeepSkyBlue)),
        _ => ("i", ResourceBrush("MutedBrush", Brushes.Gray))
    };

    private static Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

internal sealed record DialogButtonSpec(
    string Text,
    MessageBoxResult Result,
    bool IsPrimary);
