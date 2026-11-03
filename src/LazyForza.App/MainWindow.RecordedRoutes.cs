using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private event Action? RecordedRouteRecordingChanged;

    private void SetRecordedRouteRecording(bool enabled)
    {
        DeltaModule.SetRecordUnmatchedTracks(enabled);
        RecordedRouteRecordingChanged?.Invoke();
        RefreshSidebar();
    }

    private UIElement BuildRecordedRouteRecordingSwitch()
    {
        var toggle = new ToggleButton
        {
            Content = AppLocalization.Literal("记录未匹配赛道"),
            Style = (Style)FindResource("SettingSwitch")
        };
        AutomationProperties.SetAutomationId(toggle, "record-unmatched-tracks");
        AutomationProperties.SetName(toggle, AppLocalization.Literal("记录未匹配赛道"));
        void Refresh()
        {
            toggle.IsChecked = DeltaModule.RecordUnmatchedTracks;
            AutomationProperties.SetItemStatus(toggle, AppLocalization.Literal(DeltaModule.RecordUnmatchedTracks ? "开" : "关"));
        }
        toggle.Click += (_, _) =>
        {
            try { SetRecordedRouteRecording(toggle.IsChecked == true); }
            catch (Exception exception)
            {
                Refresh();
                AppDialog.Show(AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message),
                    AppLocalization.Literal("记录未匹配赛道"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        toggle.Loaded += (_, _) => { RecordedRouteRecordingChanged += Refresh; Refresh(); };
        toggle.Unloaded += (_, _) => RecordedRouteRecordingChanged -= Refresh;
        Refresh();
        return new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            Children = { WithQuickSettingPin(toggle, SidebarQuickSettings.RecordUnmatchedTracks) }
        };
    }

    private async Task<bool> RefreshAfterLapDeletionAsync(LapAnalysisModule module, Guid trackId)
    {
        try
        {
            await module.FlushPendingLapsAsync();
            if (!module.HasCurrentCompetitionSession && store.LoadTrack(trackId) is null) module.ClearTrackSelection();
            return true;
        }
        catch (Exception exception)
        {
            module.RefreshSelectedTrackHistory();
            AppDialog.Show(this, AppLocalization.Literal(exception.Message), AppLocalization.Literal("操作失败"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            RenderSelectedPage(true);
            return false;
        }
    }

    private Border RecordedRouteActions(LapAnalysisModule module, TrackTemplate track)
    {
        var complete = module.VisibleLaps.Any(lap => lap.TrackId == track.Id && lap.IsValid &&
            lap.TrackRevision == LapTrackRevision.Create(track));
        return BuildRecordedRouteActions(complete, async () =>
        {
            await module.EnrollRecordedRouteAsync(track.Id);
            RenderSelectedPage(true);
        }, exception => AppDialog.Show(this, AppLocalization.Literal(exception.Message),
            AppLocalization.Literal("操作失败"), MessageBoxButton.OK, MessageBoxImage.Error),
            () => AssignRecordedRouteAsync(module, track), async () =>
            {
                if (AppDialog.ShowChoice(this, AppLocalization.Format("route.delete.confirm", "删除“{0}”及其 {1} 条圈记录？",
                        AppLocalization.Literal(track.Name), store.CountLaps(track.Id)), AppLocalization.Literal("删除路线记录"),
                        AppLocalization.Literal("删除"), AppLocalization.Literal("取消"), MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await module.DeleteRecordedRouteAsync(track.Id);
                selectedLapIds.Clear(); displayedLapIds.Clear(); RenderSelectedPage(true);
            }, !module.HasCurrentCompetitionSession);
    }

    private async Task AssignRecordedRouteAsync(LapAnalysisModule module, TrackTemplate track)
    {
        await module.FlushPendingLapsAsync();
        var destinations = store.ListTracks(CurrentTrackSource).Where(summary => !summary.IsRecordOnly &&
                summary.TimingKind == TrackTimingKind.GameEvent).Select(summary => store.LoadTrack(summary.Id))
            .Where(saved => saved is not null).Select(saved => saved!.Value.Track).ToArray();
        var history = store.LoadLapHistory(track.Id);
        var dialog = new RecordedRouteAssignmentWindow(this, track, history.Count, destinations, partial: !history.Any(lap => lap.IsValid));
        if (dialog.ShowDialog() != true || dialog.TargetId is not Guid targetId) return;
        await module.AssociateRecordedRouteAsync(track.Id, targetId, dialog.Force);
        selectedLapIds.Clear(); displayedLapIds.Clear(); RenderSelectedPage(true);
    }

    internal static Border BuildRecordedRouteActions(bool complete, Func<Task> enrollRoute, Action<Exception> reportFailure,
        Func<Task>? assign = null, Func<Task>? delete = null, bool canManage = true)
    {
        var content = new StackPanel();
        content.Children.Add(Label("未收录路线", 14, FontWeights.SemiBold));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var enroll = AnalysisButton("收录到个人赛道库");
        enroll.IsEnabled = complete;
        if (!complete) enroll.ToolTip = AppLocalization.Literal("需要完整圈");
        enroll.HorizontalAlignment = HorizontalAlignment.Left;
        Wire(enroll, enrollRoute);
        actions.Children.Add(enroll);
        AddAction("归入已有赛道", assign);
        AddAction("删除路线记录", delete);
        content.Children.Add(actions);
        return AnalysisCard(content);

        void AddAction(string label, Func<Task>? action)
        {
            if (action is null) return;
            var button = AnalysisButton(label); button.IsEnabled = canManage;
            if (!canManage) button.ToolTip = AppLocalization.Literal("请在比赛结束后管理路线记录。");
            Wire(button, action); actions.Children.Add(button);
        }
        void Wire(Button button, Func<Task> action)
        {
            button.Margin = new Thickness(0, 0, 8, 6);
            button.Click += async (_, _) =>
            {
                actions.IsEnabled = false;
                try { await action(); }
                catch (Exception exception) { reportFailure(exception); }
                finally { actions.IsEnabled = true; }
            };
        }
    }
}

internal static class RecordedLapText
{
    internal static string? Reason(string? reason) => reason is null ? null : AppLocalization.Literal(reason switch
    {
        "recording-missing-start" => "缺少起点数据",
        "recording-missing-finish" => "缺少终点数据",
        "recording-insufficient-data" => "记录数据不足",
        "recording-sample-gap" => "遥测数据中断",
        "recording-position-jump" => "车辆位置跳变",
        "recording-vehicle-changed" => "记录中车辆发生变化",
        "recording-rewind" => "记录中发生倒带",
        "recording-sample-limit" => "已达到单圈记录上限",
        "recording-invalid-sample" => "遥测数据异常",
        "recording-route-gap" => "路线数据不完整",
        "recording-route-mismatch" => "与归入赛道不匹配",
        _ => reason
    });
    internal static bool IsPartial(string? reason) => reason?.StartsWith("recording-", StringComparison.Ordinal) == true;
}
