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
        if (!HasFreshLiveFh6Telemetry())
        {
            MessageBox.Show(
                "当前没有收到 Forza Horizon 6 的 Live 遥测。\n\n" +
                "请先打开游戏并启用 Data Out。收到实时数据后再设置 Overlay，" +
                "软件才能以游戏当前窗口为基准准确定位。\n\n" +
                "本次不会打开布局设置界面。",
                "尚未收到 FH6 遥测",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                "开始前请先在 Forza Horizon 6 中关闭“失去焦点时暂停”：\n\n" +
                "设置 → 抬头显示与游戏 → 失去焦点时暂停\n\n" +
                "回到 LazyForza 点击“是”之前，请让游戏保持在驾驶状态。" +
                "确认后 LazyForza 会将 FH6 窗口置于最前，并在其上方打开灰色布局设置层。\n\n" +
                "现在开始设置吗？",
                "设置 Overlay 布局",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        if (!ForzaHorizonWindow.TryFind(out var forzaWindow))
        {
            MessageBox.Show(
                "已经收到 FH6 遥测，但没有找到可用的 Forza Horizon 6 窗口。\n\n" +
                "请确认游戏窗口没有最小化。建议使用窗口化或无边框窗口模式后重试。",
                "未找到 FH6 窗口",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        trigger.IsEnabled = false;
        var previousState = WindowState;
        try
        {
            _ = ForzaHorizonWindow.TryActivate(forzaWindow);
            await Task.Delay(220);
            Hide();
            var result = overlay.EditLayout(forzaWindow);
            if (result is null) return;
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(result));
            await overlay.SetLayoutAsync(result, CancellationToken.None);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法打开 Overlay 布局设置：{exception.Message}",
                "布局设置失败",
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
