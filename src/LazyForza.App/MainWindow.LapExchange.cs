using System.Windows;
using System.Windows.Controls;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Telemetry;
using Microsoft.Win32;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private Border BuildLapAnalysisExchangeCard(
        LapAnalysisModule module,
        out Button exportSelected)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(Label("圈速文件", 16, FontWeights.SemiBold));
        copy.Children.Add(Label(
            ".lfzlap 保留圈速、走线、动态遥测和玩家代号；导入后可直接参与本地对比。",
            11,
            FontWeights.Normal,
            "MutedBrush"));
        layout.Children.Add(copy);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0)
        };
        var import = new Button
        {
            Content = "导入圈速",
            Padding = new Thickness(14, 7, 14, 7)
        };
        import.Click += async (_, _) => await ImportLapAnalysisAsync(module, import);
        actions.Children.Add(import);
        var exportButton = new Button
        {
            Content = "导出所选",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(10, 0, 0, 0),
            IsEnabled = selectedLapIds.Count > 0,
            ToolTip = "从圈速表格中勾选 1–4 圈"
        };
        exportButton.Click += async (_, _) =>
            await ExportSelectedLapAnalysisAsync(exportButton);
        exportSelected = exportButton;
        actions.Children.Add(exportButton);
        Grid.SetColumn(actions, 1);
        layout.Children.Add(actions);
        return Card(layout);
    }

    private async Task ExportSelectedLapAnalysisAsync(Button button)
    {
        var laps = store.LoadLapsByIds(selectedLapIds)
            .OrderBy(lap => lap.StartedAt)
            .Take(4)
            .ToArray();
        if (laps.Length == 0)
        {
            MessageBox.Show(
                this,
                AppLocalization.Text("lap.exchange.selectFirst", "请先在已保存圈速中勾选 1–4 圈。"),
                AppLocalization.Text("lap.exchange.exportTitle", "导出圈速"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var trackId = laps[0].TrackId;
        if (laps.Any(lap => lap.TrackId != trackId) || store.LoadTrack(trackId) is not { } saved)
        {
            MessageBox.Show(
                this,
                AppLocalization.Text("lap.exchange.incompleteTrack", "所选圈速的赛道数据不完整，无法导出。"),
                AppLocalization.Text("lap.exchange.exportTitle", "导出圈速"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var playerCode = CurrentPlayerCode();
        laps = laps.Select(lap => string.IsNullOrWhiteSpace(lap.PlayerCode)
                ? lap with { PlayerCode = playerCode }
                : lap)
            .ToArray();
        var dialog = new SaveFileDialog
        {
            Title = AppLocalization.Text("lap.exchange.exportDialog", "导出 LazyForza 圈速分析"),
            Filter = AppLocalization.Text("lap.exchange.fileFilter", "LazyForza 圈速分析 (*.lfzlap)|*.lfzlap"),
            DefaultExt = ".lfzlap",
            AddExtension = true,
            FileName =
                $"LazyForza-圈速-{SafeFileName(saved.Track.Name)}-" +
                $"{DateTime.Now:yyyyMMdd-HHmm}.lfzlap"
        };
        if (dialog.ShowDialog(this) != true) return;

        button.IsEnabled = false;
        try
        {
            await LapAnalysisExchangeFile.WriteAsync(
                dialog.FileName,
                saved.Track.Id,
                saved.Track.Name,
                saved.Track.Direction,
                laps[0].SectorSchemaVersion,
                playerCode,
                laps,
                lifetimeCancellation.Token);
            MessageBox.Show(
                this,
                AppLocalization.Format("lap.exchange.exported", "已导出 {0} 圈：\n{1}", laps.Length, dialog.FileName),
                AppLocalization.Text("common.exportComplete", "导出完成"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppLocalization.Format("lap.exchange.exportFailedMessage", "无法导出圈速：{0}", exception.Message),
                AppLocalization.Text("common.exportFailed", "导出失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = selectedLapIds.Count > 0;
        }
    }

    private async Task ImportLapAnalysisAsync(
        LapAnalysisModule module,
        Button? button,
        string? sourcePath = null)
    {
        if (sourcePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = AppLocalization.Text("lap.exchange.importDialog", "导入 LazyForza 圈速分析"),
                Filter = AppLocalization.Text(
                    "lap.exchange.openFilter",
                    "LazyForza 圈速分析 (*.lfzlap)|*.lfzlap|所有文件 (*.*)|*.*"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            sourcePath = dialog.FileName;
        }

        if (button is not null) button.IsEnabled = false;
        try
        {
            var package = await LapAnalysisExchangeFile.ReadAsync(
                sourcePath,
                lifetimeCancellation.Token);
            if (store.LoadTrack(package.TrackId) is not { } saved)
                throw new InvalidOperationException(
                    AppLocalization.Format(
                        "lap.exchange.missingTrack",
                        "本机没有“{0}”的赛道数据。请先导入或录入对应赛道。",
                        package.TrackName));
            if (saved.Track.Direction != package.Direction ||
                saved.Sectors.All(sector => sector.SectorSchemaVersion != package.SectorSchemaVersion))
                throw new InvalidOperationException(AppLocalization.Text(
                    "lap.exchange.trackMismatch",
                    "本机赛道与圈速文件的方向或分段版本不一致。"));

            var players = package.Laps
                .Select(lap => PlayerCodeText(lap.PlayerCode ?? package.ExportedByPlayerCode))
                .Distinct(StringComparer.CurrentCulture)
                .ToArray();
            var playerText = string.Join(AppLocalization.Text("common.listSeparator", "、"), players);
            if (MessageBox.Show(
                    this,
                    AppLocalization.Format(
                        "lap.exchange.importConfirmation",
                        "赛道：{0}\n圈速：{1} 圈\n玩家代号：{2}\n\n导入后将加入本地圈速对比。",
                        saved.Track.Name,
                        package.Laps.Count,
                        playerText),
                    AppLocalization.Text("literal:导入圈速", "导入圈速"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) != MessageBoxResult.OK) return;

            var existingIds = store.LoadLapsByIds(package.Laps.Select(lap => lap.Id).ToArray())
                .Select(lap => lap.Id)
                .ToHashSet();
            var imported = package.Laps
                .Where(lap => !existingIds.Contains(lap.Id))
                .Select(lap => lap with
                {
                    PlayerCode = NullIfBlank(PlayerIdentitySettings.Normalize(
                        lap.PlayerCode ?? package.ExportedByPlayerCode))
                })
                .ToArray();
            foreach (var lap in imported) store.SaveLap(lap);

            if (module.Snapshot is not null)
            {
                if (module.CurrentTrack?.Id == package.TrackId)
                    module.RefreshSelectedTrackHistory();
                else if (!module.HasCurrentCompetitionSession)
                    module.SelectTrack(package.TrackId);
            }
            selectedLapIds.Clear();
            selectedLapIds.UnionWith(imported.Select(lap => lap.Id));
            displayedLapIds.Clear();
            RenderSelectedPage(true);
            MessageBox.Show(
                this,
                imported.Length == 0
                    ? AppLocalization.Format(
                        "lap.exchange.alreadyImported",
                        "文件中的圈速已存在。\n玩家代号：{0}",
                        playerText)
                    : AppLocalization.Format(
                        "lap.exchange.imported",
                        "已导入 {0} 圈。\n玩家代号：{1}",
                        imported.Length,
                        playerText),
                AppLocalization.Text("common.importComplete", "导入完成"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                AppLocalization.Text("lap.exchange.importFailed", "无法导入圈速"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private string? CurrentPlayerCode()
    {
        var value = PlayerIdentitySettings.Normalize(
            store.GetAppSetting(PlayerIdentitySettings.PlayerCodeSettingKey));
        return NullIfBlank(value);
    }

    private static string PlayerCodeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? AppLocalization.Text("common.unlabeled", "未标注")
            : value.Trim();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
