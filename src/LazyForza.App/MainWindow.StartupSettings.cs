using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement BuildStartupSettingsCard()
    {
        var profile = startupProfileStore.Load();
        var panel = new StackPanel();
        panel.Children.Add(Label(
            AppLocalization.Text("settings.app.title", "语言、数据与关闭方式"),
            17,
            FontWeights.SemiBold));
        var description = Label(
            AppLocalization.Text("settings.app.description", "管理界面语言、数据存储位置和主窗口关闭行为。"),
            11,
            FontWeights.Normal,
            "MutedBrush");
        description.Margin = new Thickness(0, 4, 0, 15);
        panel.Children.Add(description);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var language = new ComboBox
        {
            ItemsSource = AppLocalization.SupportedLanguages,
            DisplayMemberPath = nameof(AppLanguageOption.NativeName),
            SelectedItem = AppLocalization.SupportedLanguages.First(option =>
                option.Code.Equals(profile.Language, StringComparison.OrdinalIgnoreCase)),
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddRow(
            AppLocalization.Text("settings.app.language", "界面语言"),
            language,
            0);

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
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddRow(
            AppLocalization.Text("settings.app.storage", "数据存储目录"),
            storage,
            1);
        var selectedDataDirectory = profile.DataDirectory;
        var chooseFolder = new Button
        {
            Content = AppLocalization.Text("settings.app.chooseFolder", "选择文件夹"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetRow(chooseFolder, 1);
        Grid.SetColumn(chooseFolder, 2);
        form.Children.Add(chooseFolder);
        var storagePath = Label(profile.DataDirectory, 10, FontWeights.Normal, "MutedBrush");
        storagePath.Margin = new Thickness(0, 5, 0, 12);
        storagePath.TextTrimming = TextTrimming.CharacterEllipsis;
        storagePath.TextWrapping = TextWrapping.NoWrap;
        Grid.SetRow(storagePath, 2);
        Grid.SetColumn(storagePath, 1);
        Grid.SetColumnSpan(storagePath, 2);
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.Children.Add(storagePath);
        storage.SelectionChanged += (_, _) =>
        {
            if (storage.SelectedItem is not DataDirectoryChoice choice) return;
            if (choice.Path is not null)
            {
                selectedDataDirectory = choice.Path;
                storagePath.Text = choice.Path;
            }
            else
            {
                storagePath.Text = selectedDataDirectory;
            }
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
            storagePath.Text = selectedDataDirectory;
        };

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
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddRow(
            AppLocalization.Text("settings.app.close", "点击关闭窗口时"),
            closeBehavior,
            3);
        panel.Children.Add(form);

        var note = Label(
            AppLocalization.Format(
                "settings.app.currentData",
                "当前正在使用：{0}\n更换语言或数据目录后需重启；切换目录不会自动搬移现有数据。",
                directories.Root),
            10,
            FontWeights.Normal,
            "MutedBrush");
        note.Margin = new Thickness(0, 8, 0, 10);
        panel.Children.Add(note);
        var save = new Button
        {
            Content = AppLocalization.Text("settings.app.save", "保存启动设置"),
            HorizontalAlignment = HorizontalAlignment.Left,
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
                CloseBehavior = selectedCloseBehavior
            };
            startupProfileStore.Save(updatedProfile);
            var restartRequired =
                !selectedLanguage.Equals(profile.Language, StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(normalizedDataDirectory, directories.Root);
            save.Content = AppLocalization.Text("literal:已保存", "已保存");
            if (restartRequired)
                MessageBox.Show(
                    AppLocalization.Text("settings.app.restartMessage", "设置已保存，重启 LazyForza 后应用语言和数据目录。"),
                    AppLocalization.Text("settings.app.restartTitle", "需要重启"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            profile = updatedProfile;
        };
        panel.Children.Add(save);
        return Card(panel);

        void AddRow(string label, Control control, int row)
        {
            while (form.RowDefinitions.Count <= row)
                form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = Label(label, 12, FontWeights.SemiBold, "MutedBrush");
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(0, 0, 12, 12);
            Grid.SetRow(caption, row);
            form.Children.Add(caption);
            control.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }
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
