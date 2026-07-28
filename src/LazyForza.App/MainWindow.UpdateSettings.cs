using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private Border BuildUpdateSettingsCard()
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < 4; index++)
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        panel.Children.Add(Label("应用更新", 17, FontWeights.SemiBold));
        var updateToggle = new ToggleButton
        {
            IsChecked = updateManager.CheckOnStartup,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 7, 12, 7)
        };
        void RefreshUpdateToggle() =>
            updateToggle.Content = updateToggle.IsChecked == true ? "启动检查：开" : "启动检查：关";
        RefreshUpdateToggle();
        updateToggle.Click += (_, _) =>
        {
            updateManager.CheckOnStartup = updateToggle.IsChecked == true;
            RefreshUpdateToggle();
        };
        Grid.SetColumn(updateToggle, 1);
        panel.Children.Add(updateToggle);

        var sourceLabel = Label("首选更新源", 12, FontWeights.SemiBold);
        sourceLabel.Margin = new Thickness(0, 12, 16, 8);
        Grid.SetRow(sourceLabel, 1);
        panel.Children.Add(sourceLabel);
        var sourceSelector = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Right,
            SelectedValuePath = nameof(ComboBoxItem.Tag)
        };
        sourceSelector.Items.Add(new ComboBoxItem
        {
            Content = "GitCode（中国大陆优先）",
            Tag = UpdateSourceKind.GitCode
        });
        sourceSelector.Items.Add(new ComboBoxItem
        {
            Content = "GitHub",
            Tag = UpdateSourceKind.GitHub
        });
        sourceSelector.SelectedValue = updateManager.PreferredSource;
        sourceSelector.SelectionChanged += (_, _) =>
        {
            if (sourceSelector.SelectedValue is not UpdateSourceKind source) return;
            updateManager.PreferredSource = source;
        };
        Grid.SetRow(sourceSelector, 1);
        Grid.SetColumn(sourceSelector, 1);
        panel.Children.Add(sourceSelector);

        var updateStatus = Label(
            $"当前版本 {CurrentApplicationVersion()} · " +
            $"首选 {updateManager.PreferredSourceName}，失败时自动尝试 {updateManager.FallbackSourceName}。 " +
            (updateManager.CanInstallAutomatically
                ? "发现新版后由你确认，程序不会强制更新。"
                : "开发构建仅检查版本，不覆盖开发目录。"),
            12,
            FontWeights.Normal,
            "MutedBrush");
        updateStatus.Margin = new Thickness(0, 8, 18, 12);
        Grid.SetRow(updateStatus, 2);
        Grid.SetColumnSpan(updateStatus, 2);
        panel.Children.Add(updateStatus);

        var checkNow = new Button
        {
            Content = "立即检查",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8)
        };
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            updateStatus.Text =
                $"正在连接 {updateManager.PreferredSourceName}，必要时使用 {updateManager.FallbackSourceName}…";
            try
            {
                var release = await updateManager.CheckAsync(lifetimeCancellation.Token);
                if (release is null)
                {
                    updateStatus.Text =
                        $"已是最新版本 {CurrentApplicationVersion()} · 首选更新源 {updateManager.PreferredSourceName}。";
                }
                else
                {
                    await OfferUpdateAsync(release, updateStatus);
                }
            }
            catch (OperationCanceledException)
            {
                updateStatus.Text = "已取消检查。";
            }
            catch (Exception exception)
            {
                updateManager.ReportFailure("Manual update check failed", exception);
                updateStatus.Text = $"检查失败：{exception.Message}";
            }
            finally
            {
                checkNow.IsEnabled = true;
            }
        };
        Grid.SetRow(checkNow, 3);
        Grid.SetColumnSpan(checkNow, 2);
        panel.Children.Add(checkNow);
        return Card(panel);
    }
}
