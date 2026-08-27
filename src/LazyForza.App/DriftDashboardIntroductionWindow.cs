using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LazyForza.App;

internal sealed class DriftDashboardIntroductionWindow : Window
{
    private readonly CheckBox autoCloseDashboard;

    public DriftDashboardIntroductionWindow(bool autoCloseDashboardEnabled)
    {
        Title = "漂移仪表盘（实验性功能）";
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ResourceBrush("WindowBrush");
        Foreground = ResourceBrush("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        stack.Children.Add(new TextBlock
        {
            Text = "漂移仪表盘 · 实验性功能",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(Paragraph(
            "它会用 FH6 官方 UDP 中的本车速度、侧滑、偏航和驾驶输入，优先判断 Spin 风险，并用方向箭头、换挡箭头和颜色区间提供练习辅助。控车余量与积分速度趋势均为 LazyForza 推导，不是游戏漂移分数，辅助作用有限。",
            new Thickness(0, 10, 0, 0)));
        stack.Children.Add(Section(
            "实验性功能 · 辅助能力有限",
            "Spin 风险、方向修正和换挡建议仍需结合更多真实 FH6 漂移数据持续校准，只能作为有限的练习辅助。换挡箭头用于降低失控风险，不代表车辆的最佳换挡点；请勿把当前结果视为游戏计分、裁判结论或稳定控车的保证。"));

        stack.Children.Add(Section(
            "先控车，再增加角度",
            "底部 SPIN 色带越靠近红色，失控风险越高；此时优先按方向和换挡图形降低风险。绿色状态下，积分速度格会随侧滑角增大而增加，但不建议为了填满格数强行扩大角度。"));

        stack.Children.Add(Section(
            "建议减少信息重叠",
            "漂移仪表盘可以和主仪表盘同时显示，但练习时建议先关闭主仪表盘，把注意力留给防 Spin 色带、方向箭头、换挡箭头和侧滑角。"));
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
            Content = "了解并开启实验性功能",
            MinWidth = 112,
            Padding = new Thickness(16, 7, 16, 7),
            FontWeight = FontWeights.SemiBold,
            IsDefault = true
        };
        enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(enable);
        stack.Children.Add(buttons);
        Content = stack;
        AppLocalization.ApplyTo(this);
    }

    public bool AutoCloseDashboard =>
        autoCloseDashboard.IsChecked == true;

    private static UIElement Section(string title, string description)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = AppLocalization.Literal(title),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("AccentBrush")
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
        Text = AppLocalization.Literal(text),
        Margin = margin,
        FontSize = 12.5,
        LineHeight = 21,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ResourceBrush("MutedBrush")
    };

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.FindResource(key);
}
