using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed class UpdateOfferWindow : Window
{
    public UpdateOfferWindow(
        Window owner,
        UpdateReleaseInfo release,
        bool canInstallAutomatically)
    {
        Owner = owner;
        Title = "发现新版本";
        Width = 520;
        Height = 410;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("PanelBrush");
        Foreground = ResourceBrush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "发现新版本",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var badge = BuildTypeBadge(release.Type);
        Grid.SetColumn(badge, 1);
        heading.Children.Add(badge);
        root.Children.Add(heading);

        var version = new StackPanel { Margin = new Thickness(0, 10, 0, 13) };
        version.Children.Add(new TextBlock
        {
            Text = $"LazyForza {release.Version.ToString(3)}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        version.Children.Add(new TextBlock
        {
            Text = $"更新来源：{release.SourceName}",
            FontSize = 12,
            Foreground = ResourceBrush("MutedBrush"),
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetRow(version, 1);
        root.Children.Add(version);

        var notesPanel = new Grid();
        notesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        notesPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        notesPanel.Children.Add(new TextBlock
        {
            Text = "更新内容",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(1, 0, 0, 7)
        });
        var notes = new TextBox
        {
            Text = UpdateReleaseMetadata.ToDisplayText(release.Notes),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = ResourceBrush("CardBrush"),
            Foreground = ResourceBrush("TextBrush"),
            BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            FontSize = 13,
            IsTabStop = true
        };
        Grid.SetRow(notes, 1);
        notesPanel.Children.Add(notes);
        Grid.SetRow(notesPanel, 2);
        root.Children.Add(notesPanel);

        var footer = new Grid { Margin = new Thickness(0, 13, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = canInstallAutomatically
                ? "本次更新可以稍后安装，不会强制更新。"
                : "当前为开发构建，仅展示更新信息，不执行自动安装。",
            FontSize = 12,
            Foreground = ResourceBrush("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0)
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var later = new Button
        {
            Content = canInstallAutomatically ? "稍后" : "知道了",
            MinWidth = 76,
            IsCancel = true
        };
        actions.Children.Add(later);
        if (canInstallAutomatically)
        {
            var install = new Button
            {
                Content = "下载并安装",
                MinWidth = 112,
                IsDefault = true,
                Background = ResourceBrush("AccentBrush"),
                BorderBrush = ResourceBrush("AccentBrush")
            };
            install.Click += (_, _) => DialogResult = true;
            actions.Children.Add(install);
        }
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
    }

    private static Border BuildTypeBadge(UpdateReleaseType type)
    {
        var accent = type switch
        {
            UpdateReleaseType.MajorFeature => Color.FromRgb(167, 139, 250),
            UpdateReleaseType.Feature => Color.FromRgb(32, 184, 207),
            UpdateReleaseType.Fix => Color.FromRgb(57, 217, 138),
            _ => Color.FromRgb(154, 164, 178)
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new TextBlock
            {
                Text = type.DisplayName(),
                Foreground = new SolidColorBrush(accent),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.FindResource(key);
}
