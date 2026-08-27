using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Domain;
using LazyForza.Overlay;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private async Task ConfigureOverlayLayoutAsync(Button trigger)
    {
        var hasFreshTelemetry = HasFreshLiveFh6Telemetry();
        var hasForzaWindow = ForzaHorizonWindow.TryFind(out var targetWindow);
        string? setupNotice = null;

        if (!hasFreshTelemetry)
        {
            setupNotice = hasForzaWindow
                ? AppLocalization.Text("overlay.layout.previewNotice", "当前未收到 FH6 遥测，HUD 使用示例数据。建议让游戏保持在驾驶画面并启用 Data Out 后再校准布局。")
                : AppLocalization.Text("overlay.layout.previewNoticeWithoutWindow", "当前未收到 FH6 遥测，也未找到游戏窗口，HUD 使用示例数据。建议打开游戏并进入驾驶画面后再校准布局。");
            var telemetryPrompt = hasForzaWindow
                ? AppLocalization.Text("overlay.layout.noTelemetryPrompt", "当前没有收到 Forza Horizon 6 的 Live 遥测，但已经找到游戏窗口。\n\n" +
                  "仍可使用示例 HUD 打开布局编辑器。建议先让游戏保持在驾驶画面、启用 Data Out，" +
                  "并关闭“失去焦点时暂停”，这样更容易确认实际遮挡和位置。\n\n" +
                  "现在仍要打开布局设置吗？")
                : AppLocalization.Text("overlay.layout.noTelemetryOrWindowPrompt", "当前没有收到 Forza Horizon 6 的 Live 遥测，也没有找到可用的游戏窗口。\n\n" +
                  "仍可在当前显示器上使用示例 HUD 编辑布局，但画布可能与游戏实际窗口不同。" +
                  "建议先打开游戏并进入驾驶画面后再设置。\n\n" +
                  "现在仍要打开布局设置吗？");
            if (AppDialog.Show(
                    telemetryPrompt,
                    AppLocalization.Literal("尚未收到 FH6 遥测"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            if (!hasForzaWindow)
                targetWindow = ForzaHorizonWindow.CreatePreviewTarget(this);
        }
        else
        {
            if (!hasForzaWindow)
            {
                AppDialog.Show(
                    AppLocalization.Text("overlay.layout.windowNotFoundMessage", "已经收到 FH6 遥测，但没有找到可用的 Forza Horizon 6 窗口。\n\n" +
                    "请确认游戏窗口没有最小化。建议使用窗口化或无边框窗口模式后重试。"),
                    AppLocalization.Literal("未找到 FH6 窗口"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (AppDialog.Show(
                    AppLocalization.Text("overlay.layout.setupPrompt", "开始前请先在 Forza Horizon 6 中关闭“失去焦点时暂停”：\n\n" +
                    "设置 → 抬头显示与游戏 → 失去焦点时暂停\n\n" +
                    "回到 LazyForza 点击“是”之前，请让游戏保持在驾驶状态。" +
                    "确认后 LazyForza 会将 FH6 窗口置于最前，并在其上方打开灰色布局设置层。\n\n" +
                    "现在开始设置吗？"),
                    AppLocalization.Literal("设置 Overlay 布局"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
        }

        trigger.IsEnabled = false;
        var previousState = WindowState;
        try
        {
            if (hasForzaWindow)
            {
                _ = ForzaHorizonWindow.TryActivate(targetWindow);
                await Task.Delay(220);
            }
            Hide();
            var result = overlay.EditLayout(targetWindow, setupNotice);
            if (result is null) return;
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(result));
            await overlay.SetLayoutAsync(result, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppDialog.Show(
                AppLocalization.Format("overlay.layout.openFailed", "无法打开 Overlay 布局设置：{0}", exception.Message),
                AppLocalization.Literal("布局设置失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Show();
            WindowState = previousState == WindowState.Minimized
                ? WindowState.Normal
                : previousState;
            Activate();
            trigger.IsEnabled = true;
            RenderSelectedPage();
        }
    }

    private bool HasFreshLiveFh6Telemetry()
    {
        if (sourceKind != TelemetrySourceKind.Live) return false;
        var diagnostics = telemetry.Diagnostics;
        return diagnostics.State == TelemetryStreamState.Live &&
               diagnostics.ValidPackets > 0 &&
               diagnostics.LastPacketAt is { } lastPacket &&
               DateTimeOffset.UtcNow - lastPacket <= TimeSpan.FromSeconds(2.5);
    }
}
