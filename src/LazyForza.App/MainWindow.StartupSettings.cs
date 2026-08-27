using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement BuildStartupSettingsCard()
    {
        var profile = startupProfileStore.Load();
        var panel = new StackPanel();
        panel.Children.Add(Label(
            AppLocalization.Text("settings.app.title", "界面、数据与关闭方式"),
            17,
            FontWeights.SemiBold));
        var description = Label(
            AppLocalization.Text("settings.app.description", "管理界面语言、强调色、数据存储位置和主窗口关闭行为。"),
            11,
            FontWeights.Normal,
            "MutedBrush");
        description.Margin = new Thickness(0, 4, 0, 15);
        panel.Children.Add(description);

        var language = new ComboBox
        {
            ItemsSource = AppLocalization.SupportedLanguages,
            DisplayMemberPath = nameof(AppLanguageOption.NativeName),
            SelectedItem = AppLocalization.SupportedLanguages.First(option =>
                option.Code.Equals(profile.Language, StringComparison.OrdinalIgnoreCase)),
            MinWidth = 280,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        panel.Children.Add(StartupSettingRow(
            AppLocalization.Text("settings.app.language", "界面语言"),
            AppLocalization.Text("settings.app.languageDetail", "选择主界面与初始化指引使用的语言。"),
            language));

        var selectedAccentColor = profile.AccentColor;
        var accentButtons = new Dictionary<AppAccentColor, ToggleButton>();
        var accentPicker = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-4, -4, 0, 0)
        };
        foreach (var definition in AppAccentColors.Definitions)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(definition.Color),
                BorderBrush = definition.Value == AppAccentColor.PureWhite
                    ? Brush("BorderBrush")
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0)
            });
            content.Children.Add(Label(definition.DisplayName, 11, FontWeights.SemiBold));
            var button = new ToggleButton
            {
                Content = content,
                IsChecked = definition.Value == selectedAccentColor,
                MinWidth = 112,
                Padding = new Thickness(9, 8, 9, 8),
                Margin = new Thickness(3),
                Tag = definition.Value
            };
            button.Click += (_, _) =>
            {
                selectedAccentColor = (AppAccentColor)button.Tag;
                foreach (var (value, candidate) in accentButtons)
                    candidate.IsChecked = value == selectedAccentColor;
                AppAccentColors.Apply(selectedAccentColor);
                profile = profile with { AccentColor = selectedAccentColor };
                startupProfileStore.Save(profile);
            };
            accentButtons.Add(definition.Value, button);
            accentPicker.Children.Add(button);
        }
        panel.Children.Add(StartupSettingRow(
            AppLocalization.Text("settings.app.accent", "UI 强调色"),
            AppLocalization.Text("settings.app.accentDetail", "用于高亮、选中状态和主要交互提示。"),
            accentPicker));

        var storageChoices = new[]
        {
            new DataDirectoryChoice(
                AppLocalization.Text("settings.app.storageDefault", "LazyForza（默认）"),
                StartupProfileStore.DefaultDataDirectory),
            new DataDirectoryChoice("LazyForza-Release", StartupProfileStore.ReleaseDataDirectory),
            new DataDirectoryChoice(
                AppLocalization.Text("settings.app.storageProgram", "程序目录 · Data"),
                StartupProfileStore.ProgramDataDirectory),
            new DataDirectoryChoice(
                AppLocalization.Text("settings.app.storageCustom", "自定义文件夹"),
                IsKnownPath(profile.DataDirectory) ? null : profile.DataDirectory)
        };
        var storage = new ComboBox
        {
            ItemsSource = storageChoices,
            DisplayMemberPath = nameof(DataDirectoryChoice.Name),
            SelectedItem = storageChoices.First(choice =>
                choice.Path is not null && PathsEqual(choice.Path, profile.DataDirectory)) ?? storageChoices[^1],
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var selectedDataDirectory = profile.DataDirectory;
        var chooseFolder = new Button
        {
            Content = AppLocalization.Text("settings.app.chooseFolder", "选择文件夹"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(10, 0, 0, 0)
        };
        var storagePicker = new Grid();
        storagePicker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        storagePicker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        storagePicker.Children.Add(storage);
        Grid.SetColumn(chooseFolder, 1);
        storagePicker.Children.Add(chooseFolder);
        var storagePath = Label(string.Empty, 10, FontWeights.Normal, "MutedBrush");
        storagePath.Margin = new Thickness(2, 7, 0, 0);
        storagePath.TextTrimming = TextTrimming.CharacterEllipsis;
        storagePath.TextWrapping = TextWrapping.NoWrap;
        var storageControl = new StackPanel();
        storageControl.Children.Add(storagePicker);
        storageControl.Children.Add(storagePath);
        UpdateStoragePath();
        storage.SelectionChanged += (_, _) =>
        {
            if (storage.SelectedItem is not DataDirectoryChoice choice) return;
            if (choice.Path is not null)
                selectedDataDirectory = choice.Path;
            UpdateStoragePath();
        };
        chooseFolder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = AppLocalization.Text("settings.app.chooseFolderDialog", "选择 LazyForza 数据目录"),
                InitialDirectory = Directory.Exists(selectedDataDirectory)
                    ? selectedDataDirectory
                    : StartupProfileStore.DefaultDataDirectory,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            selectedDataDirectory = Path.GetFullPath(dialog.FolderName);
            storageChoices[^1] = new DataDirectoryChoice(
                AppLocalization.Text("settings.app.storageCustom", "自定义文件夹"),
                selectedDataDirectory);
            storage.ItemsSource = null;
            storage.ItemsSource = storageChoices;
            storage.DisplayMemberPath = nameof(DataDirectoryChoice.Name);
            storage.SelectedItem = storageChoices[^1];
            UpdateStoragePath();
        };
        panel.Children.Add(StartupSettingRow(
            AppLocalization.Text("settings.app.storage", "数据存储目录"),
            AppLocalization.Text("settings.app.storageDetail", "圈速、录制、赛道、设置与缓存统一保存在此。"),
            storageControl));

        var closeChoices = new[]
        {
            new CloseBehaviorChoice(
                AppLocalization.Text("settings.app.closeTray", "最小化到托盘"),
                MainWindowCloseBehavior.MinimizeToTray),
            new CloseBehaviorChoice(
                AppLocalization.Text("settings.app.closeExit", "关闭程序"),
                MainWindowCloseBehavior.ExitApplication)
        };
        var closeBehavior = new ComboBox
        {
            ItemsSource = closeChoices,
            DisplayMemberPath = nameof(CloseBehaviorChoice.Name),
            SelectedItem = closeChoices.First(choice => choice.Value == profile.CloseBehavior),
            MinWidth = 280,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        panel.Children.Add(StartupSettingRow(
            AppLocalization.Text("settings.app.close", "点击关闭窗口时"),
            AppLocalization.Text("settings.app.closeDetail", "决定主窗口右上角关闭按钮的行为。"),
            closeBehavior));

        var footer = new Grid { Margin = new Thickness(2, 3, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Label(
            AppLocalization.Text(
                "settings.app.restartNote",
                "语言和数据目录在重启后生效；切换目录不会搬移现有数据。"),
            10,
            FontWeights.Normal,
            "MutedBrush");
        note.VerticalAlignment = VerticalAlignment.Center;
        note.Margin = new Thickness(0, 0, 18, 0);
        footer.Children.Add(note);
        var save = new Button
        {
            Content = AppLocalization.Text("settings.app.save", "保存启动设置"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 8, 16, 8)
        };
        save.Click += (_, _) =>
        {
            var selectedLanguage = ((AppLanguageOption)language.SelectedItem).Code;
            var selectedCloseBehavior = ((CloseBehaviorChoice)closeBehavior.SelectedItem).Value;
            var normalizedDataDirectory = Path.GetFullPath(selectedDataDirectory);
            var updatedProfile = profile with
            {
                Language = selectedLanguage,
                DataDirectory = normalizedDataDirectory,
                CloseBehavior = selectedCloseBehavior,
                AccentColor = selectedAccentColor
            };
            startupProfileStore.Save(updatedProfile);
            var restartRequired =
                !selectedLanguage.Equals(profile.Language, StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(normalizedDataDirectory, directories.Root);
            save.Content = AppLocalization.Text("literal:已保存", "已保存");
            if (restartRequired)
                AppRestartPrompt.Show(
                    this,
                    AppLocalization.Text(
                        "settings.app.restartMessage",
                        "设置已保存，重启 LazyForza 后应用语言和数据目录。"));
            profile = updatedProfile;
        };
        Grid.SetColumn(save, 1);
        footer.Children.Add(save);
        panel.Children.Add(footer);
        return Card(panel);

        void UpdateStoragePath()
        {
            storagePath.Text = AppLocalization.Format(
                "settings.app.selectedData",
                "已选择：{0}",
                selectedDataDirectory);
        }
    }

    private static Border StartupSettingRow(string title, string detail, FrameworkElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Label(title, 12, FontWeights.SemiBold));
        var detailLabel = Label(detail, 10, FontWeights.Normal, "MutedBrush");
        detailLabel.Margin = new Thickness(0, 4, 18, 0);
        copy.Children.Add(detailLabel);
        row.Children.Add(copy);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 0, 0, 10),
            Child = row
        };
    }

    private static bool IsKnownPath(string path) =>
        PathsEqual(path, StartupProfileStore.DefaultDataDirectory) ||
        PathsEqual(path, StartupProfileStore.ReleaseDataDirectory) ||
        PathsEqual(path, StartupProfileStore.ProgramDataDirectory);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record DataDirectoryChoice(string Name, string? Path)
    {
        public override string ToString() => Name;
    }

    private sealed record CloseBehaviorChoice(string Name, MainWindowCloseBehavior Value)
    {
        public override string ToString() => Name;
    }
}
