using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LazyForza.App;

internal sealed class DriftDashboardIntroductionWindow : Window
{
    private readonly CheckBox autoCloseDashboard;

    public DriftDashboardIntroductionWindow(bool autoCloseDashboardEnabled)
    {
        Title = "漂移仪表盘（Preview）";
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 245));
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        stack.Children.Add(new TextBlock
        {
            Text = "漂移仪表盘 · Preview",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(Paragraph(
            "它会用 FH6 官方 UDP 中的本车速度、侧滑、偏航和驾驶输入，显示侧滑角、稳定度、连续稳定时长及实时练习建议。稳定度是 LazyForza 的辅助推导，不是游戏漂移分数。",
            new Thickness(0, 10, 0, 0)));
        stack.Children.Add(Section(
            "当前为开发预览功能",
            "漂移识别阈值、稳定度和提示策略仍会结合真实 FH6 漂移数据继续校准。请将它作为练习辅助，不要把当前结果视为游戏计分或裁判结论。"));

        stack.Children.Add(Section(
            "建议减少信息重叠",
            "漂移仪表盘可以和主仪表盘同时显示，但练习时建议先关闭主仪表盘，把注意力留给侧滑角、方向和油门。"));
        autoCloseDashboard = new CheckBox
        {
            Content = "打开漂移仪表盘时自动关闭主仪表盘",
            IsChecked = autoCloseDashboardEnabled,
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground
        };
        stack.Children.Add(autoCloseDashboard);

        stack.Children.Add(Section(
            "圈速数据保护",
            "漂移仪表盘开启期间，圈速分析会自动停止，之后的圈速不会写入数据库。关闭漂移仪表盘后，会按你原来的模块偏好恢复。"));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        var cancel = new Button
        {
            Content = "暂不开启",
            MinWidth = 96,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var enable = new Button
        {
            Content = "了解并开启 Preview",
            MinWidth = 112,
            Padding = new Thickness(16, 7, 16, 7),
            FontWeight = FontWeights.SemiBold,
            IsDefault = true
        };
        enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(enable);
        stack.Children.Add(buttons);
        Content = stack;
    }

    public bool AutoCloseDashboard =>
        autoCloseDashboard.IsChecked == true;

    private static UIElement Section(string title, string description)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 184, 207))
        });
        stack.Children.Add(Paragraph(
            description,
            new Thickness(0, 5, 0, 0)));
        return stack;
    }

    private static TextBlock Paragraph(
        string text,
        Thickness margin) => new()
    {
        Text = text,
        Margin = margin,
        FontSize = 12.5,
        LineHeight = 21,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(188, 195, 203))
    };
}
