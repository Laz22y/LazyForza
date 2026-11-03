using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed class HudLayoutProfilesPanel : Border
{
    private sealed record LayoutChoice(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    internal static string Title => AppLocalization.Text("hud.profiles.title", "HUD 布局方案");

    internal HudLayoutProfilesPanel(OverlayLayoutProfileStore profiles,
        Func<Guid, Task> select, Func<string, bool, Task> saveName, Func<Task> delete, UIElement? pin = null)
    {
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8);
        Padding = new Thickness(18); Margin = new Thickness(0, 0, 0, 14);
        var panel = new StackPanel(); Child = panel;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (pin is not null) { DockPanel.SetDock(pin, Dock.Right); heading.Children.Add(pin); }
        var saved = Text(AppLocalization.Text("hud.profiles.autoSave", "自动保存"), 11, "MutedBrush");
        DockPanel.SetDock(saved, Dock.Right); heading.Children.Add(saved);
        var title = Text(Title, 17); title.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(title); panel.Children.Add(heading);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var selector = new ComboBox
        {
            ItemsSource = profiles.State.Profiles.Select(profile => new LayoutChoice(profile.Id, profile.Name)).ToArray(),
            DisplayMemberPath = nameof(LayoutChoice.Name),
            SelectedValuePath = nameof(LayoutChoice.Id), SelectedValue = profiles.State.ActiveId,
            MinWidth = 120, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(selector, Title);
        row.Children.Add(selector);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1); row.Children.Add(actions); panel.Children.Add(row);
        var create = ActionButton(AppLocalization.Text("hud.profiles.new", "新建方案"));
        var rename = IconButton("\uE70F", AppLocalization.Text("hud.profiles.rename", "重命名方案"));
        var remove = IconButton("\uE74D", AppLocalization.Text("hud.profiles.delete", "删除方案"));
        create.IsEnabled = profiles.State.Profiles.Count < OverlayLayoutProfileStore.MaximumProfiles;
        remove.IsEnabled = profiles.State.Profiles.Count > 1;
        actions.Children.Add(create); actions.Children.Add(rename); actions.Children.Add(remove);

        var editRow = new Grid { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { MaxLength = OverlayLayoutProfileStore.MaximumNameLength,
            Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(name, AppLocalization.Text("hud.profiles.name", "方案名称"));
        editRow.Children.Add(name);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(editActions, 1); editRow.Children.Add(editActions);
        var accept = ActionButton(AppLocalization.Literal("保存"));
        accept.SetResourceReference(StyleProperty, "PrimaryActionButton");
        var cancel = ActionButton(AppLocalization.Literal("取消"));
        editActions.Children.Add(accept); editActions.Children.Add(cancel);
        panel.Children.Add(editRow);
        var error = Text("", 11, "DangerBrush");
        error.Margin = new Thickness(0, 8, 0, 0); error.Visibility = Visibility.Collapsed;
        panel.Children.Add(error);
        var renaming = false;
        var busy = false;

        async Task Run(Func<Task> operation)
        {
            if (busy) return;
            busy = true; row.IsEnabled = false; editRow.IsEnabled = false;
            error.Visibility = Visibility.Collapsed;
            try { await operation(); }
            catch (Exception exception)
            {
                selector.SelectedValue = profiles.State.ActiveId;
                error.Text = AppLocalization.Format("hud.profiles.failed", "未能保存布局方案：{0}", exception.Message);
                error.Visibility = Visibility.Visible;
            }
            finally { row.IsEnabled = true; editRow.IsEnabled = true; busy = false; }
        }

        void ValidateName()
        {
            accept.IsEnabled = profiles.CanUseName(name.Text, renaming ? profiles.State.ActiveId : null);
            error.Visibility = string.IsNullOrWhiteSpace(name.Text) || accept.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            error.Text = AppLocalization.Text("hud.profiles.nameInUse", "名称已使用或包含无效字符");
        }
        void BeginName(bool renameExisting)
        {
            renaming = renameExisting;
            var number = 1;
            string candidate;
            do { candidate = AppLocalization.Format("hud.profiles.numberedName", "布局 {0}", number++); }
            while (!profiles.CanUseName(candidate));
            name.Text = renameExisting ? profiles.Active.Name : candidate;
            accept.Content = renameExisting ? AppLocalization.Literal("保存") : AppLocalization.Text("hud.profiles.create", "创建");
            editRow.Visibility = Visibility.Visible;
            selector.IsEnabled = false; actions.IsEnabled = false;
            ValidateName(); name.Focus(); name.SelectAll();
        }
        void CancelName()
        {
            editRow.Visibility = Visibility.Collapsed; error.Visibility = Visibility.Collapsed;
            selector.IsEnabled = true; actions.IsEnabled = true; selector.Focus();
        }
        selector.SelectionChanged += async (_, _) =>
        {
            if (!busy && selector.SelectedValue is Guid id && id != profiles.State.ActiveId)
                await Run(() => select(id));
        };
        create.Click += (_, _) => BeginName(false);
        rename.Click += (_, _) => BeginName(true);
        cancel.Click += (_, _) => CancelName();
        name.TextChanged += (_, _) => ValidateName();
        accept.Click += async (_, _) => await Run(() => saveName(name.Text.Trim(), renaming));
        name.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { CancelName(); args.Handled = true; }
            else if (args.Key == Key.Enter && accept.IsEnabled)
            { args.Handled = true; await Run(() => saveName(name.Text.Trim(), renaming)); }
        };
        remove.Click += async (_, _) =>
        {
            if (AppDialog.Show(AppLocalization.Format("hud.profiles.deletePrompt", "删除布局方案“{0}”？", profiles.Active.Name),
                AppLocalization.Text("hud.profiles.delete", "删除方案"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes) await Run(delete);
        };
    }

    private static TextBlock Text(string text, double size, string brush = "TextBrush")
    {
        var block = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush); return block;
    }

    private static Button ActionButton(string label) => new()
    { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 6, 0) };

    private static Button IconButton(string glyph, string label)
    {
        var button = new Button { Content = new TextBlock { Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14 },
            ToolTip = label, Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        AutomationProperties.SetName(button, label); return button;
    }
}
