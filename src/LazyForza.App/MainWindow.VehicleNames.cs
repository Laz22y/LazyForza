using System.Windows;
using System.Windows.Controls;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private readonly VehicleNameUpdateManager vehicleNameUpdates;
    private bool checkingVehicleNames;
    private string? vehicleNameUpdateStatus;
    private Action? refreshVehicleNameSettings;

    public async Task CheckStartupUpdatesAsync()
    {
        await CheckForUpdatesOnStartupAsync();
        if (!lifetimeCancellation.IsCancellationRequested)
            await CheckVehicleNamesAsync(manual: false);
    }

    private Border BuildVehicleNameSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(VehicleNameUpdateWindow.T("车辆名称库", "Vehicle name catalog"), 17, FontWeights.SemiBold));
        var summary = Label("", 12, FontWeights.Normal, "MutedBrush");
        summary.Margin = new Thickness(0, 8, 0, 8);
        summary.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(summary);
        var explanation = Label(VehicleNameUpdateWindow.T(
            "每次启动检查 GitHub 上游修订，确认后才下载。离线时使用本地数据；手动命名不会被覆盖。",
            "Checks GitHub revisions at startup and asks before downloading. Local names remain available offline; custom names are preserved."), 12, FontWeights.Normal, "MutedBrush");
        explanation.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(explanation);
        var status = Label("", 12, FontWeights.Normal, "MutedBrush");
        status.Margin = new Thickness(0, 8, 0, 0);
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);
        var check = new Button { Content = VehicleNameUpdateWindow.T("检查车辆名称更新", "Check vehicle name updates"),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(4, 12, 4, 4) };
        check.Click += async (_, _) => await CheckVehicleNamesAsync(manual: true);
        panel.Children.Add(check);
        refreshVehicleNameSettings = () =>
        {
            var info = vehicleNameUpdates.Snapshot.Info;
            summary.Text = info is null ? VehicleNameUpdateWindow.T("当前没有可用名称数据。", "No vehicle names are available.") :
                VehicleNameUpdateWindow.T("已收录车辆：", "Vehicles: ") + info.VehicleCount + " · " + VehicleNameUpdateWindow.Date(info.UpdatedAt) +
                "\n" + VehicleNameUpdateWindow.T("修订：", "Revision: ") + info.Revision;
            status.Text = vehicleNameUpdateStatus ?? string.Empty;
            status.Visibility = string.IsNullOrEmpty(vehicleNameUpdateStatus) ? Visibility.Collapsed : Visibility.Visible;
            check.IsEnabled = !checkingVehicleNames;
        };
        refreshVehicleNameSettings();
        return Card(panel);
    }

    private async Task CheckVehicleNamesAsync(bool manual)
    {
        if (checkingVehicleNames || lifetimeCancellation.IsCancellationRequested) return;
        checkingVehicleNames = true;
        var downloadRequested = false;
        SetStatus(VehicleNameUpdateWindow.T("正在检查 GitHub 车辆名称库…", "Checking vehicle names on GitHub…"));
        try
        {
            var result = await vehicleNameUpdates.CheckAsync(lifetimeCancellation.Token);
            if (lifetimeCancellation.IsCancellationRequested) return;
            if (result.State != VehicleNameUpdateState.Available)
            {
                SetStatus(result.State == VehicleNameUpdateState.Ignored
                    ? VehicleNameUpdateWindow.T("已忽略此上游修订；出现新修订时会再次提示。", "This revision is ignored. A future revision will be offered.")
                    : VehicleNameUpdateWindow.T("车辆名称库已是最新。", "The vehicle name catalog is up to date."));
                return;
            }
            var offer = new VehicleNameUpdateWindow(this, vehicleNameUpdates.Snapshot.Info, result.Revision);
            offer.ShowDialog();
            if (lifetimeCancellation.IsCancellationRequested) return;
            if (offer.Choice == VehicleNameUpdateChoice.Ignore)
            {
                vehicleNameUpdates.Ignore(result.Revision);
                SetStatus(VehicleNameUpdateWindow.T("已忽略此修订；后续新修订仍会提示。", "Revision ignored. Future revisions will still be offered."));
                return;
            }
            if (offer.Choice != VehicleNameUpdateChoice.Download)
            {
                SetStatus(VehicleNameUpdateWindow.T("已暂缓更新；下次启动会再次提示，也可手动检查。", "Update deferred. Check manually or wait for the next launch."));
                return;
            }
            downloadRequested = true;
            SetStatus(VehicleNameUpdateWindow.T("正在下载并校验车辆名称…", "Downloading and validating vehicle names…"));
            using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            var progress = new VehicleNameDownloadWindow(this, downloadCancellation);
            VehicleNameSnapshot snapshot;
            progress.Show();
            try { snapshot = await vehicleNameUpdates.InstallAsync(result.Revision, downloadCancellation.Token); }
            catch (OperationCanceledException) when (!lifetimeCancellation.IsCancellationRequested && downloadCancellation.IsCancellationRequested)
            {
                SetStatus(VehicleNameUpdateWindow.T("已取消下载，现有名称库保持不变。", "Download cancelled. Existing names are unchanged."));
                return;
            }
            finally { progress.Close(); }
            VehicleNameCatalog.Activate(snapshot);
            SetStatus(VehicleNameUpdateWindow.T("车辆名称库已更新，自动名称已刷新，手动名称保持不变。", "Vehicle names updated. Automatic names refreshed; custom names preserved."));
            if (!lifetimeCancellation.IsCancellationRequested) RenderSelectedPage(true);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            updateManager.ReportFailure("Vehicle name catalog update failed", error);
            SetStatus(VehicleNameUpdateWindow.T("检查或下载未完成，继续使用已有缓存或内置名称库。可稍后重试。",
                "Check or download failed. Cached or bundled names remain available. Try again later."));
            if ((manual || downloadRequested) && !lifetimeCancellation.IsCancellationRequested)
                AppDialog.Show(this, vehicleNameUpdateStatus!, VehicleNameUpdateWindow.T("车辆名称库", "Vehicle name catalog"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            checkingVehicleNames = false;
            refreshVehicleNameSettings?.Invoke();
        }

        void SetStatus(string value) { vehicleNameUpdateStatus = value; refreshVehicleNameSettings?.Invoke(); }
    }

    internal static string VehicleProfileDisplayName(VehicleProfileSummary profile, int index, int count) =>
        profile.CustomName ?? (count > 1
            ? VehicleDisplayName(profile.Fingerprint.CarOrdinal) + " · " + AppLocalization.Format("shift.tuneName", "调校 {0}", index + 1)
            : VehicleDisplayName(profile.Fingerprint.CarOrdinal));
}
