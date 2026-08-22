using System.IO;
using System.Windows;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    internal async Task OpenAssociatedFileAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            MessageBox.Show(
                this,
                AppLocalization.Format("files.notFound", "找不到文件：\n{0}", fullPath),
                AppLocalization.Text("files.openFailed", "无法打开文件"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        switch (Path.GetExtension(fullPath).ToLowerInvariant())
        {
            case ".lfztelemetry":
                pendingReplayRecordingPath = fullPath;
                navigation.SelectedIndex = 5;
                RenderSelectedPage();
                break;
            case ".lfzlap":
                navigation.SelectedIndex = 4;
                RenderSelectedPage();
                await ImportLapAnalysisAsync(
                    moduleManager.Modules.OfType<LapAnalysisModule>().Single(),
                    button: null,
                    sourcePath: fullPath);
                break;
            case ".lfzestate":
                navigation.SelectedIndex = 6;
                RenderSelectedPage();
                ImportEstateTrackPackage(fullPath);
                break;
            default:
                MessageBox.Show(
                    this,
                    AppLocalization.Text("files.unsupported", "该文件类型不能由 LazyForza 直接打开。"),
                    AppLocalization.Text("files.openFailed", "无法打开文件"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
        }
    }

    private void ImportEstateTrackPackage(
        string path,
        EstateTrackPackageService? packageService = null)
    {
        packageService ??= new EstateTrackPackageService(store, CurrentApplicationVersion());
        try
        {
            var preview = packageService.Preview(path);
            var confirmation = MessageBox.Show(
                this,
                AppLocalization.Format(
                    "estate.import.confirmation",
                    "地图：{0}\n修订：{1}\n路线：{2:0.00} km，{3} 个圈速分段，{4} 个检查点\n\n文件只会导入赛道路线、终点门、检查点和维修区定义，不包含圈速记录或个人配置。确认导入吗？",
                    preview.Track.Name,
                    preview.Definition.MapRevision,
                    preview.Track.LengthMeters / 1000,
                    preview.Sectors.Count,
                    preview.Definition.Checkpoints.Count),
                AppLocalization.Text("literal:导入地产环道", "导入地产环道"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;

            var result = packageService.Import(path, CurrentTrackSource);
            if (result.AlreadyExists && !result.ExistingTrackMatches)
            {
                var replace = MessageBox.Show(
                    this,
                    AppLocalization.Format(
                        "estate.import.conflict",
                        "本机已有相同赛道标识，但路线或比赛设置与文件不同。\n\n是否用文件中的“{0}”替换本机版本？\n替换会删除这条赛道已有的圈速记录；其他赛道和用户数据不受影响。",
                        preview.Track.Name),
                    AppLocalization.Text("estate.import.conflictTitle", "赛道标识冲突"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (replace == MessageBoxResult.Yes)
                    result = packageService.Import(
                        path,
                        CurrentTrackSource,
                        replaceExisting: true);
            }

            MessageBox.Show(
                this,
                result.Imported
                    ? AppLocalization.Format(
                        "estate.import.imported",
                        "已导入“{0}”。请确认游戏中加载的是对应地图和修订版本，再手动开始计时。",
                        result.TrackName)
                    : result.ExistingTrackMatches
                        ? result.SourceUpdated
                            ? AppLocalization.Format(
                                "estate.import.sourceUpdated",
                                "“{0}”的赛道内容已经存在，现已修复其本地归属，可在赛道页面正常看到。",
                                result.TrackName)
                            : AppLocalization.Format(
                                "estate.import.alreadyExists",
                                "“{0}”已经存在且内容一致，未重复导入。",
                                result.TrackName)
                        : AppLocalization.Format(
                            "estate.import.keptLocal",
                            "“{0}”使用了相同标识，但内容不同；你已保留本机版本。",
                            result.TrackName),
                AppLocalization.Text("literal:导入地产环道", "导入地产环道"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            trackPreviewCache.Clear();
            RenderSelectedPage();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                AppLocalization.Text("literal:导入地产环道", "导入地产环道"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
