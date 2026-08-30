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
            IsEnabled = !updateManager.IsUpdateMandatory,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 7, 12, 7)
        };
        void RefreshUpdateToggle() =>
            updateToggle.Content = updateManager.IsUpdateMandatory
                ? AppLocalization.Text(
                    "settings.update.previewForced",
                    "启动检查：强制开启")
                : AppLocalization.Format(
                    "settings.update.checkOnStartup",
                    "启动检查：{0}",
                    AppLocalization.Literal(updateToggle.IsChecked == true ? "开" : "关"));
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
            Content = AppLocalization.Text("settings.update.gitcode", "GitCode（中国大陆优先）"),
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
            updateManager.IsUpdateMandatory
                ? AppLocalization.Format(
                    "settings.update.statusPreview",
                    "当前预览版 {0} · 每次启动强制检查；首选 {1}，失败时自动尝试 {2}。只检测并安装更高预览版，正式版不会进入此通道。",
                    CurrentApplicationVersion(),
                    updateManager.PreferredSourceName,
                    updateManager.FallbackSourceName)
                : AppLocalization.Format(
                    updateManager.CanInstallAutomatically
                        ? "settings.update.statusInstall"
                        : "settings.update.statusDevelopment",
                    updateManager.CanInstallAutomatically
                        ? "当前版本 {0} · 首选 {1}，失败时自动尝试 {2}。发现新版后由你确认，程序不会强制更新。"
                        : "当前版本 {0} · 首选 {1}，失败时自动尝试 {2}。开发构建仅检查版本，不覆盖开发目录。",
                    CurrentApplicationVersion(),
                    updateManager.PreferredSourceName,
                    updateManager.FallbackSourceName),
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
            updateStatus.Text = AppLocalization.Format(
                "settings.update.checking",
                "正在连接 {0}，必要时使用 {1}…",
                updateManager.PreferredSourceName,
                updateManager.FallbackSourceName);
            try
            {
                if (updateManager.IsUpdateMandatory)
                {
                    await EnforcePreviewUpdateAsync(updateStatus);
                    return;
                }
                var release = await updateManager.CheckAsync(lifetimeCancellation.Token);
                if (release is null)
                {
                    updateStatus.Text = AppLocalization.Format(
                        "settings.update.current",
                        "已是最新版本 {0} · 首选更新源 {1}。",
                        CurrentApplicationVersion(),
                        updateManager.PreferredSourceName);
                }
                else
                {
                    await OfferUpdateAsync(release, updateStatus);
                }
            }
            catch (OperationCanceledException)
            {
                updateStatus.Text = AppLocalization.Literal("已取消检查。");
            }
            catch (Exception exception)
            {
                updateManager.ReportFailure("Manual update check failed", exception);
                updateStatus.Text = AppLocalization.Format(
                    "settings.update.failed",
                    "检查失败：{0}",
                    AppLocalization.Literal(exception.Message));
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
