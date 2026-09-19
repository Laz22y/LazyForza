using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed class LapRecordEditor : Window
{
    private readonly TextBox name, notes;
    private readonly CheckBox favorite, reference;
    internal LapAnnotation Annotation => new(name.Text, notes.Text, favorite.IsChecked == true, reference.IsChecked == true);

    internal LapRecordEditor(Window? owner, LapSummary lap)
    {
        if (owner is not null) Owner = owner;
        Title = AppLocalization.Literal("管理圈记录"); Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = (Brush)Application.Current.Resources["TextBrush"];
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Text(Title, 21));
        root.Children.Add(Text($"{lap.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {lap.TotalSeconds:0.000} s", 12, true));
        root.Children.Add(Text("名称", 13));
        name = new TextBox { Text = lap.Annotation.Name ?? "", MaxLength = LapAnnotation.MaximumNameLength };
        AutomationProperties.SetName(name, AppLocalization.Literal("名称"));
        root.Children.Add(name);
        root.Children.Add(Text("备注", 13));
        notes = new TextBox { Text = lap.Annotation.Notes ?? "", MaxLength = LapAnnotation.MaximumNotesLength,
            Height = 108, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(notes, AppLocalization.Literal("备注"));
        root.Children.Add(notes);
        favorite = new CheckBox { Content = AppLocalization.Literal("收藏 · 自动清理时保留"), IsChecked = lap.Annotation.IsFavorite,
            Margin = new Thickness(0, 18, 0, 12) };
        reference = new CheckBox { Content = AppLocalization.Literal("固定为参考"), IsChecked = lap.Annotation.IsReference,
            IsEnabled = lap.IsValid && double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0 };
        root.Children.Add(favorite); root.Children.Add(reference);
        root.Children.Add(Text("同路线和车辆条件兼容时优先使用；会替换该条件下原有的固定参考。", 12, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = AppLocalization.Literal("取消"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0), MinWidth = 80 };
        var save = new Button { Content = AppLocalization.Literal("保存"), IsDefault = true, MinWidth = 80 };
        save.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(save); root.Children.Add(actions); Content = root;
    }

    private static TextBlock Text(string value, int size, bool muted = false) => new()
    {
        Text = AppLocalization.Literal(value), FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"], Margin = new Thickness(0, 10, 0, 6)
    };
}
