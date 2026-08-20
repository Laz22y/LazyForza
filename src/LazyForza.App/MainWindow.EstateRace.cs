using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.EstateRace;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement EstateRacePage()
    {
        var module = moduleManager.Modules.OfType<EstateRaceModule>().Single();
        var estate = moduleManager.Modules.OfType<EstateCircuitModule>().Single();
        var packageService = new LazyForza.Storage.EstateTrackPackageService(store, CurrentApplicationVersion());
        var savedProfile = module.LoadSavedProfileAsync(lifetimeCancellation.Token).GetAwaiter().GetResult();
        var stack = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        var heading = new StackPanel();
        heading.Children.Add(Label("地产赛事", 28, FontWeights.SemiBold));
        var description = Label(
            "连接自托管赛事房间，以参赛车手或 OB 身份加入。房间支持 1–12 名车手；OB 不参赛，只接收实时赛事数据用于观赛和转播。",
            14, FontWeights.Normal, "MutedBrush");
        description.Margin = new Thickness(0, 5, 24, 0);
        heading.Children.Add(description);
        header.Children.Add(heading);
        var enterRoom = new Button
        {
            Content = "进入房间",
            MinWidth = 116,
            Padding = new Thickness(18, 9, 18, 9),
            VerticalAlignment = VerticalAlignment.Top,
            FontWeight = FontWeights.SemiBold
        };
        stack.Children.Add(header);

        var statusCard = new Grid();
        statusCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var statusCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var statusTitle = Label("尚未进入房间", 20, FontWeights.SemiBold);
        var statusText = Label(module.State.ConnectionText, 13, FontWeights.Normal, ConnectionBrush(module.State.ConnectionState));
        statusText.Margin = new Thickness(0, 5, 0, 0);
        statusCopy.Children.Add(statusTitle);
        statusCopy.Children.Add(statusText);
        var connectedProfile = Label("", 12, FontWeights.Normal, "MutedBrush");
        connectedProfile.Margin = new Thickness(0, 6, 0, 0);
        statusCopy.Children.Add(connectedProfile);
        statusCard.Children.Add(statusCopy);

        TextBlock phaseValue = null!;
        TextBlock flagValue = null!;
        TextBlock onlineValue = null!;
        TextBlock fastestValue = null!;
        StackPanel participantList = null!;
        StackPanel participantRows = null!;
        TextBlock participantTitle = null!;
        TextBlock strategyTitle = null!;
        TextBlock strategySummary = null!;
        TextBlock strategyConfidence = null!;
        TextBlock strategyWindow = null!;
        TextBlock strategyPitLoss = null!;
        TextBlock strategyPitLossSource = null!;
        TextBlock strategyPace = null!;
        TextBlock strategyTrend = null!;
        TextBlock strategyEvidence = null!;
        TextBlock practiceStorage = null!;
        Dictionary<EstatePracticeTestKind, EstatePracticeTestControls> practiceControls = null!;
        Border practiceCard = null!;
        StackPanel connectedContent = null!;
        Border hostingGuide = null!;
        var exportResult = new Button
        {
            Content = "导出我的成绩 PNG",
            Padding = new Thickness(14, 7, 14, 7),
            Visibility = Visibility.Collapsed
        };
        var ready = new Button
        {
            Content = "标记已准备",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var statusActions = new StackPanel
        {
            Margin = new Thickness(28, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        enterRoom.HorizontalAlignment = HorizontalAlignment.Stretch;
        statusActions.Children.Add(enterRoom);
        ready.HorizontalAlignment = HorizontalAlignment.Stretch;
        statusActions.Children.Add(ready);
        Grid.SetColumn(statusActions, 1);
        statusCard.Children.Add(statusActions);
        stack.Children.Add(Card(statusCard));

        enterRoom.Click += async (_, _) =>
        {
            if (module.State.IsConnected ||
                module.State.ConnectionState == EstateRaceConnectionState.Reconnecting &&
                module.State.Session is not null)
            {
                await module.DisconnectAsync();
                UpdateRacePageState();
                return;
            }
            var dialog = new EstateRaceJoinWindow(savedProfile, EstateRaceModule.ReadServerDescriptorAsync) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Profile is not { } profile) return;
            enterRoom.IsEnabled = false;
            try
            {
                var descriptor = await EstateRaceModule.ReadServerDescriptorAsync(profile.ServerAddress, lifetimeCancellation.Token);
                if (descriptor.ProtocolVersion != EstateRaceModule.ProtocolVersion)
                    throw new InvalidOperationException("服务端协议版本与当前 LazyForza 不兼容。");
                if (profile.IsObserver && !descriptor.SupportsObservers)
                    throw new InvalidOperationException("该服务端版本不支持 OB 身份，请让房主更新服务端。");
                if (!string.IsNullOrWhiteSpace(descriptor.ActiveTrackId))
                {
                    if (!Guid.TryParse(descriptor.ActiveTrackId, out var trackId))
                        throw new InvalidOperationException("服务端配置的赛道标识不是有效 UUID，请房主在总控中重新填写。");
                    var localTrack = store.LoadTrack(trackId);
                    if (localTrack is null)
                    {
                        await DownloadHostedEstateTrackAsync(
                            profile.ServerAddress, descriptor, packageService, estate,
                            replaceExisting: false, lifetimeCancellation.Token);
                    }
                    else if (string.Equals(localTrack.Value.Track.Source, "EstateRaceServer", StringComparison.Ordinal))
                    {
                        store.UpdateTrackSource(trackId, CurrentTrackSource);
                        trackPreviewCache.Clear();
                    }
                    var identity = packageService.Identify(trackId);
                    if (!string.IsNullOrWhiteSpace(descriptor.ActiveTrackPackageHash) &&
                        !identity.Matches(descriptor.ActiveTrackPackageHash))
                    {
                        await DownloadHostedEstateTrackAsync(
                            profile.ServerAddress, descriptor, packageService, estate,
                            replaceExisting: true, lifetimeCancellation.Token);
                        identity = packageService.Identify(trackId);
                    }
                    if (!string.IsNullOrWhiteSpace(descriptor.ActiveTrackPackageHash) &&
                        !identity.Matches(descriptor.ActiveTrackPackageHash))
                        throw new InvalidOperationException("下载后的赛道摘要仍与服务端不一致，已阻止连接。请房主重新上传赛道文件并核对 SHA-256。");
                    if (estate.ActiveDefinition?.TrackId != trackId || !estate.State.IsTimingActive)
                        estate.StartTiming(trackId);
                }
                else if (!estate.State.IsTimingActive || estate.ActiveDefinition is null)
                {
                    throw new InvalidOperationException("服务端尚未指定赛道。请先在“赛道”页面手动选择地产环道并开始计时，或请房主在总控中填写赛道标识和 SHA-256。");
                }
                if (module.Status.State != ModuleRuntimeState.Running)
                    await moduleActivation.SetEnabledAsync(EstateRaceModule.ModuleId, true, lifetimeCancellation.Token);
                await module.ConnectAsync(
                    profile,
                    lifetimeCancellation.Token,
                    descriptor.ActiveTrackPackageHash);
                if (module.State.IsConnected) savedProfile = profile with { Password = string.Empty };
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法连接地产赛事", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                enterRoom.IsEnabled = true;
                UpdateRacePageState();
            }
        };
        ready.Click += async (_, _) =>
        {
            try
            {
                var local = module.State.Session?.Participants.FirstOrDefault(item => item.Id == module.State.LocalParticipantId);
                await module.SetReadyAsync(!(local?.IsReady ?? false), lifetimeCancellation.Token);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法更新准备状态", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        exportResult.Click += (_, _) =>
        {
            try
            {
                var state = module.State;
                var session = state.Session ?? throw new InvalidOperationException("当前没有可导出的赛事结果。");
                var participant = session.Participants.FirstOrDefault(item => item.Id == state.LocalParticipantId) ??
                                  throw new InvalidOperationException("没有找到你的车手成绩。");
                var report = BuildEstateRacePersonalResultReport(session, participant);
                var phaseName = session.Phase == RaceSessionPhase.Grid ? "排位赛" : "正赛";
                var path = PngReportExporter.Export(
                    this, report,
                    $"LazyForza-{phaseName}成绩-{participant.DisplayName}-{DateTime.Now:yyyyMMdd-HHmm}.png");
                if (path is not null)
                    MessageBox.Show(this, $"已导出：\n{path}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法导出成绩", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        connectedContent = new StackPanel { Visibility = Visibility.Collapsed };
        var sessionMetrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 4) };
        phaseValue = Label("—", 22, FontWeights.SemiBold);
        flagValue = Label("—", 22, FontWeights.SemiBold);
        onlineValue = Label("0 / 12", 22, FontWeights.SemiBold);
        fastestValue = Label("—", 22, FontWeights.SemiBold, "PurpleBrush");
        sessionMetrics.Children.Add(MetricCard("赛事阶段", phaseValue, Label("等待服务端", 11, FontWeights.Normal, "MutedBrush")));
        sessionMetrics.Children.Add(MetricCard("旗语", flagValue, Label("由总控发布", 11, FontWeights.Normal, "MutedBrush")));
        sessionMetrics.Children.Add(MetricCard("在线车手", onlineValue, Label("房间上限 12 人", 11, FontWeights.Normal, "MutedBrush")));
        sessionMetrics.Children.Add(MetricCard("本场最快圈", fastestValue, Label("全体车手共同比较", 11, FontWeights.Normal, "MutedBrush")));
        connectedContent.Children.Add(sessionMetrics);

        var practicePanel = new StackPanel();
        var practiceHeader = new Grid();
        practiceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        practiceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var practiceHeading = new StackPanel();
        practiceHeading.Children.Add(Label("练习赛策略测试", 18, FontWeights.SemiBold));
        var practiceIntro = Label(
            "每次只进行一个项目。检测到回转、赛道边界、旗语、超速等异常后会自动结束；终止前已经完成且检查合格的数据仍会保存，异常圈和未完成阶段不会写入样本。暂停只允许在进站模拟的换胎区内操作。",
            11, FontWeights.Normal, "MutedBrush");
        practiceIntro.Margin = new Thickness(0, 4, 16, 0);
        practiceHeading.Children.Add(practiceIntro);
        practiceHeader.Children.Add(practiceHeading);
        practiceStorage = Label("同赛道历史样本 0 条", 11, FontWeights.SemiBold, "AccentBrush");
        practiceStorage.VerticalAlignment = VerticalAlignment.Top;
        practiceStorage.Margin = new Thickness(12, 4, 0, 0);
        Grid.SetColumn(practiceStorage, 1);
        practiceHeader.Children.Add(practiceStorage);
        practicePanel.Children.Add(practiceHeader);
        var practiceRows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        practiceControls = new Dictionary<EstatePracticeTestKind, EstatePracticeTestControls>();
        foreach (var kind in Enum.GetValues<EstatePracticeTestKind>())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var rowTop = new StackPanel { Orientation = Orientation.Horizontal };
            var rowTitle = Label("—", 14, FontWeights.SemiBold);
            var rowStatus = Label("可开始", 10, FontWeights.SemiBold, "MutedBrush");
            rowStatus.Margin = new Thickness(10, 2, 0, 0);
            rowTop.Children.Add(rowTitle);
            rowTop.Children.Add(rowStatus);
            copy.Children.Add(rowTop);
            var rowDescription = Label("—", 11, FontWeights.Normal, "MutedBrush");
            rowDescription.Margin = new Thickness(0, 3, 0, 0);
            copy.Children.Add(rowDescription);
            var rowGuidance = Label("—", 11, FontWeights.Normal, "TextBrush");
            rowGuidance.Margin = new Thickness(0, 5, 0, 0);
            copy.Children.Add(rowGuidance);
            var progress = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Margin = new Thickness(0, 7, 0, 0)
            };
            copy.Children.Add(progress);
            row.Children.Add(copy);
            var action = new Button
            {
                Content = "开始测试",
                MinWidth = 96,
                Padding = new Thickness(14, 8, 14, 8),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            action.Click += (_, _) =>
            {
                try
                {
                    if (module.State.PracticeTests?.ActiveKind == kind)
                        module.StopPracticeTest();
                    else
                        module.StartPracticeTest(kind);
                    UpdateRacePageState();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "无法更新练习测试", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
            var rowBorder = new Border
            {
                Background = Brush("SurfaceAltBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 11, 14, 11),
                Child = row
            };
            practiceRows.Children.Add(rowBorder);
            practiceControls[kind] = new EstatePracticeTestControls(
                rowTitle, rowStatus, rowDescription, rowGuidance, progress, action);
        }
        practicePanel.Children.Add(practiceRows);
        practiceCard = Card(practicePanel);
        practiceCard.Visibility = Visibility.Collapsed;
        connectedContent.Children.Add(practiceCard);

        var strategyPanel = new StackPanel();
        var strategyHeading = new Grid();
        strategyHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strategyHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strategyHeading.Children.Add(Label("进站策略预测", 18, FontWeights.SemiBold));
        strategyConfidence = Label("低置信度", 11, FontWeights.SemiBold, "MutedBrush");
        strategyConfidence.Padding = new Thickness(10, 4, 10, 4);
        strategyConfidence.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(strategyConfidence, 1);
        strategyHeading.Children.Add(strategyConfidence);
        strategyPanel.Children.Add(strategyHeading);
        strategyTitle = Label("暂不预测", 21, FontWeights.SemiBold);
        strategyTitle.Margin = new Thickness(0, 12, 0, 0);
        strategyPanel.Children.Add(strategyTitle);
        strategySummary = Label("进入正赛并完成至少三个干净圈后开始计算。", 13, FontWeights.Normal, "MutedBrush");
        strategySummary.Margin = new Thickness(0, 5, 0, 0);
        strategyPanel.Children.Add(strategySummary);

        var strategyMetrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        strategyWindow = Label("—", 18, FontWeights.SemiBold);
        strategyPitLoss = Label("—", 18, FontWeights.SemiBold);
        strategyPitLossSource = Label("等待完整进站样本", 10, FontWeights.Normal, "MutedBrush");
        strategyPace = Label("—", 18, FontWeights.SemiBold);
        strategyTrend = Label("—", 18, FontWeights.SemiBold);
        strategyMetrics.Children.Add(MetricCard("建议窗口", strategyWindow, Label("进入维修区入口线", 10, FontWeights.Normal, "MutedBrush")));
        strategyMetrics.Children.Add(MetricCard("预计进站损失", strategyPitLoss, strategyPitLossSource));
        strategyMetrics.Children.Add(MetricCard("当前代表圈", strategyPace, Label("仅采用干净圈", 10, FontWeights.Normal, "MutedBrush")));
        strategyMetrics.Children.Add(MetricCard("每圈配速趋势", strategyTrend, Label("正数表示逐圈变慢", 10, FontWeights.Normal, "MutedBrush")));
        strategyPanel.Children.Add(strategyMetrics);
        strategyEvidence = Label("尚无样本。", 11, FontWeights.Normal, "MutedBrush");
        strategyEvidence.Margin = new Thickness(2, 10, 0, 0);
        strategyPanel.Children.Add(strategyEvidence);
        var strategyNotice = Label(
            "只比较继续跑与一次虚拟换胎的预计用时，不代表 FH6 真实胎况，也不包含天气、交通、对手临场策略或未执行处罚。赛道边界失控造成的异常损时不会计入轮胎衰退趋势。",
            11, FontWeights.Normal, "WarningBrush");
        strategyNotice.Margin = new Thickness(2, 7, 0, 0);
        strategyPanel.Children.Add(strategyNotice);
        connectedContent.Children.Add(Card(strategyPanel));

        participantList = new StackPanel();
        var participantHeading = new Grid();
        participantHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        participantHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        participantTitle = Label("正赛排名", 17, FontWeights.SemiBold);
        participantHeading.Children.Add(participantTitle);
        Grid.SetColumn(exportResult, 1);
        participantHeading.Children.Add(exportResult);
        participantList.Children.Add(participantHeading);
        participantRows = new StackPanel();
        participantList.Children.Add(participantRows);
        connectedContent.Children.Add(Card(participantList));
        var gripNotice = Label(
            "抓地提示不是轮胎磨损值。FH6 UDP 不提供轮胎磨损、车损或换胎完成字段；HUD 只根据本圈轮胎滑移样本分为略微、中度、严重、极限四档。“正在维修区服务”只代表车辆进入已录入的换胎区；本机显示“维修停留完成”也仅表示连续低速停车达到设置时长。",
            12, FontWeights.Normal, "WarningBrush");
        gripNotice.Margin = new Thickness(4, 12, 8, 8);
        connectedContent.Children.Add(gripNotice);
        connectedContent.Children.Add(BuildEstateRaceHudSettingsEntry());
        stack.Children.Add(connectedContent);
        hostingGuide = BuildEstateRaceHostingGuide();
        stack.Children.Add(hostingGuide);

        void UpdateRacePageState()
        {
            var state = module.State;
            var roomAttached = state.IsConnected ||
                               state.ConnectionState == EstateRaceConnectionState.Reconnecting &&
                               state.Session is not null;
            statusText.Text = state.ConnectionText;
            statusText.Foreground = Brush(ConnectionBrush(state.ConnectionState));
            statusTitle.Text = roomAttached ? state.Session?.SessionName ?? "已进入房间" : "尚未进入房间";
            enterRoom.Content = roomAttached ? "退出房间" : "进入房间";
            connectedProfile.Text = roomAttached && module.ActiveProfile is { } profile
                ? profile.IsObserver
                    ? $"{profile.DisplayName} · OB 转播 · {profile.ServerAddress}"
                    : $"{profile.DisplayName} · {profile.TeamName ?? "个人参赛"} · {profile.ServerAddress}"
                : "进入房间时会读取服务端指定的赛道，并自动启用本机相同的地产环道。";
            ready.IsEnabled = state.IsConnected && !state.IsObserver &&
                              state.Session?.Phase is RaceSessionPhase.Lobby or RaceSessionPhase.Grid;
            ready.Visibility = state.IsConnected && !state.IsObserver ? Visibility.Visible : Visibility.Collapsed;
            connectedContent.Visibility = roomAttached ? Visibility.Visible : Visibility.Collapsed;
            hostingGuide.Visibility = roomAttached ? Visibility.Collapsed : Visibility.Visible;
            var session = state.Session;
            phaseValue.Text = session is null
                ? "—"
                : session.Phase == RaceSessionPhase.Practice && session.PracticeSessionCount > 1
                    ? $"练习赛 FP{session.PracticeSessionNumber}/{session.PracticeSessionCount}" +
                      (session.PracticeTimeExpired ? " · 已结束" : string.Empty)
                : session.Phase == RaceSessionPhase.Qualifying && session.QualifyingSessionCount > 1
                    ? $"排位赛 Q{session.QualifyingSessionNumber}/{session.QualifyingSessionCount}" +
                      (session.QualifyingTimeExpired ? " · 已结束" : string.Empty)
                    : RacePhaseLabel(session.Phase);
            flagValue.Text = session is null
                ? "—"
                : session.ChequeredImminent && session.Flag == RaceControlFlag.Green
                    ? "方格旗准备"
                    : RaceFlagLabel(session.Flag);
            flagValue.Foreground = Brush(session?.ChequeredImminent == true && session.Flag == RaceControlFlag.Green
                ? "TextBrush"
                : session?.Flag switch
            {
                RaceControlFlag.Yellow => "WarningBrush",
                RaceControlFlag.Red => "DangerBrush",
                RaceControlFlag.Chequered => "TextBrush",
                _ => "SuccessBrush"
            });
            onlineValue.Text = session is null ? "0 / 12" : $"{session.Participants.Count(item => item.IsConnected)} / 12";
            fastestValue.Text = Time(session?.FastestLapSeconds);
            var strategy = state.PitStrategy;
            strategyTitle.Text = strategy?.Title ?? "暂不预测";
            strategyTitle.Foreground = Brush(strategy?.Decision switch
            {
                EstatePitStrategyDecision.PitThisLap or EstatePitStrategyDecision.PitWindow => "WarningBrush",
                EstatePitStrategyDecision.StayOut => "SuccessBrush",
                EstatePitStrategyDecision.InPit => "AccentBrush",
                _ => "TextBrush"
            });
            strategySummary.Text = strategy?.Summary ?? "尚未收到策略样本。";
            strategyConfidence.Text = PitStrategyConfidenceText(strategy?.Confidence);
            strategyConfidence.Foreground = Brush(strategy?.Confidence switch
            {
                EstatePitStrategyConfidence.High => "SuccessBrush",
                EstatePitStrategyConfidence.Medium => "WarningBrush",
                _ => "MutedBrush"
            });
            strategyWindow.Text = PitStrategyWindowText(strategy);
            strategyPitLoss.Text = strategy?.EstimatedPitLossSeconds is double pitLoss
                ? $"+{pitLoss:0.0}s"
                : "—";
            strategyPitLossSource.Text = strategy?.EstimatedPitLossSeconds is null
                ? "等待完整进站样本"
                : strategy.PitLossSource switch
                {
                    EstatePitLossSource.CurrentSession => $"本场实测 · {strategy.ObservedPitStopCount} 次",
                    EstatePitLossSource.Historical => "同赛道历史有效进站",
                    EstatePitLossSource.ConfiguredGeometry => "维修区几何与限速估算",
                    _ => "等待完整进站样本"
                };
            strategyPace.Text = Time(strategy?.RepresentativeLapSeconds);
            strategyTrend.Text = strategy?.DegradationPerLapSeconds is double trend
                ? $"{trend:+0.000;-0.000;±0.000}s"
                : "—";
            strategyEvidence.Text = strategy is null
                ? "尚无样本。"
                : $"采用 {strategy.CleanLapCount} 个当前轮胎周期代表圈；排除 {strategy.ExcludedLapCount} 圈" +
                  $"（其中边界 {strategy.BoundaryIncidentLapCount}、异常离群 {strategy.AnomalousLapCount}、进站 {strategy.PitAffectedLapCount}；分类可能重叠）。" +
                  (strategy.HistoricalSampleCount > 0
                      ? $" 同赛道历史匹配 {strategy.HistoricalSampleCount} 条、长距离证据 {strategy.HistoricalEvidenceLapCount} 圈；{strategy.HistoricalMatchDescription}。"
                      : " 尚未匹配到同赛道历史样本。") +
                  (strategy.UsesHistoricalPace ? " 当前建议正在使用历史配速基线。" : string.Empty);

            var practiceState = state.PracticeTests;
            practiceCard.Visibility = !state.IsObserver && session?.Phase == RaceSessionPhase.Practice
                ? Visibility.Visible
                : Visibility.Collapsed;
            practiceStorage.Text = $"同赛道历史样本 {practiceState?.StoredSampleCount ?? 0} 条 · 自动轮换";
            foreach (var (kind, controls) in practiceControls)
            {
                var item = practiceState?.Items.FirstOrDefault(candidate => candidate.Kind == kind);
                if (item is null) continue;
                controls.Title.Text = item.Title;
                controls.Description.Text = item.Description;
                controls.Guidance.Text = item.Guidance;
                controls.Status.Text = PracticeTestStatusText(item.Status);
                controls.Status.Foreground = Brush(item.Status switch
                {
                    EstatePracticeTestStatus.Active => "AccentBrush",
                    EstatePracticeTestStatus.Completed => "SuccessBrush",
                    EstatePracticeTestStatus.Failed => "DangerBrush",
                    EstatePracticeTestStatus.Cancelled => "WarningBrush",
                    _ => "MutedBrush"
                });
                controls.Progress.Maximum = Math.Max(1, item.TargetSteps);
                controls.Progress.Value = Math.Clamp(item.CompletedSteps, 0, Math.Max(1, item.TargetSteps));
                controls.Action.Content = item.IsActive
                    ? "结束项目"
                    : item.Status is EstatePracticeTestStatus.Completed or EstatePracticeTestStatus.Failed or EstatePracticeTestStatus.Cancelled
                        ? "再测一次"
                        : "开始测试";
                controls.Action.IsEnabled = item.IsActive || practiceState?.ActiveKind is null;
            }
            participantRows.Children.Clear();
            participantTitle.Text = session?.Phase switch
                {
                    RaceSessionPhase.Practice => "练习赛排名",
                    RaceSessionPhase.Qualifying or RaceSessionPhase.Grid => "排位赛排名",
                    RaceSessionPhase.Finished => "正赛最终成绩",
                    _ => "正赛排名"
                };
            exportResult.Visibility = !state.IsObserver &&
                                      session?.Phase is (RaceSessionPhase.Grid or RaceSessionPhase.Finished)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (session is null || session.Participants.Count == 0)
            {
                participantRows.Children.Add(Label("尚无车手登录。", 13, FontWeights.Normal, "MutedBrush"));
                return;
            }
            foreach (var participant in session.Participants)
                participantRows.Children.Add(RaceParticipantRow(participant, state.LocalParticipantId, session, session.AllowTeams));
        }

        UpdateRacePageState();
        refreshVisiblePage = UpdateRacePageState;
        return Scroll(stack);
    }

    private async Task DownloadHostedEstateTrackAsync(
        string serverAddress,
        EstateRaceServerDescriptor descriptor,
        LazyForza.Storage.EstateTrackPackageService packageService,
        EstateCircuitModule estate,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        if (!descriptor.TrackPackageAvailable ||
            string.IsNullOrWhiteSpace(descriptor.TrackPackageDownloadPath))
        {
            throw new InvalidOperationException(
                replaceExisting
                    ? "本机赛道与服务端的 SHA-256 不一致，而且房主没有在服务端托管赛道文件。请从房主处重新获取正确的 .lfzestate 文件。"
                    : $"本机没有服务端指定的地产环道“{descriptor.ActiveTrackName ?? descriptor.ActiveTrackId}”，而且房主没有在服务端托管赛道文件。请先手动导入房主提供的 .lfzestate 文件。");
        }

        var sizeText = descriptor.TrackPackageSizeBytes is > 0
            ? $"（{descriptor.TrackPackageSizeBytes.Value / 1024d:0.#} KiB）"
            : string.Empty;
        var message = replaceExisting
            ? $"本机的“{descriptor.ActiveTrackName ?? descriptor.ActiveTrackId}”与本场赛道摘要不同。\n\n是否从服务端下载并替换？{sizeText}\n\n替换会删除这条本地赛道及其已有圈速记录，其他赛道和用户数据不受影响。"
            : $"本机没有“{descriptor.ActiveTrackName ?? descriptor.ActiveTrackId}”。\n\n是否从赛事服务端下载并导入？{sizeText}\n下载完成并校验 SHA-256 后才会进入房间。";
        if (MessageBox.Show(this, message, "下载赛事赛道", MessageBoxButton.YesNo,
                replaceExisting ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            throw new InvalidOperationException("你已取消下载赛事赛道，未进入房间。");

        const long maximumHostedPackageBytes = 1_572_864;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"LazyForza-{Guid.NewGuid():N}.lfzestate");
        try
        {
            var downloadUri = EstateRaceHttpUri(serverAddress, descriptor.TrackPackageDownloadPath);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await client.GetAsync(
                downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > maximumHostedPackageBytes)
                throw new InvalidDataException("服务端返回的赛道文件超过 1.5 MiB 托管上限。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > maximumHostedPackageBytes)
                        throw new InvalidDataException("服务端返回的赛道文件超过 1.5 MiB 托管上限。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!string.IsNullOrWhiteSpace(descriptor.TrackPackageFileSha256))
            {
                await using var file = File.OpenRead(temporaryPath);
                var fileHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!string.Equals(fileHash, descriptor.TrackPackageFileSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件的 SHA-256 与服务端描述不一致。");
            }
            var preview = packageService.Preview(temporaryPath, cancellationToken);
            if (!Guid.TryParse(descriptor.ActiveTrackId, out var expectedTrackId) ||
                preview.Manifest.TrackId != expectedTrackId ||
                !PackageMatchesHash(preview.Manifest, descriptor.ActiveTrackPackageHash))
                throw new InvalidDataException("下载文件中的赛道标识或数据摘要与房间配置不一致。");

            if (replaceExisting && estate.ActiveDefinition?.TrackId == expectedTrackId)
                estate.StopTiming();
            var imported = packageService.Import(
                temporaryPath, CurrentTrackSource, replaceExisting, cancellationToken);
            if (!imported.Imported && !imported.AlreadyExists)
                throw new InvalidOperationException("赛事赛道未能导入本机数据库。");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool PackageMatchesHash(
        LazyForza.Storage.EstateTrackPackageManifest manifest,
        string? expectedHash) =>
        !string.IsNullOrWhiteSpace(expectedHash) &&
        (string.Equals(manifest.TrackFingerprintSha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(manifest.PayloadSha256, expectedHash, StringComparison.OrdinalIgnoreCase));

    private static Uri EstateRaceHttpUri(string serverAddress, string path)
    {
        var normalized = serverAddress.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal)) normalized = "http://" + normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var server))
            throw new InvalidOperationException("服务端地址无效。");
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme switch { "ws" => "http", "wss" => "https", _ => server.Scheme },
            Path = path.StartsWith('/') ? path : "/" + path,
            Query = string.Empty
        };
        return builder.Uri;
    }

    private Border BuildEstateRaceHostingGuide()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("自己开一个赛事房间", 20, FontWeights.SemiBold));
        panel.Children.Add(Label(
            "服务端不需要一直在线。比赛前启动，比赛结束后关掉即可；房间最多 12 人，普通家用电脑、小型云主机或 Cloudflare Workers 都够用。",
            13, FontWeights.Normal, "MutedBrush"));

        var methods = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cloudflare = HostingMethod(
            "Cloudflare 一键部署",
            "适合没有服务器的房主。登录自己的 Cloudflare 账号，打开部署页并确认创建 Worker 与 Durable Object。部署完成后，先访问分配到的域名，在网页里设置房间密码和总控密码，再把域名发给车手。",
            "打开一键部署页",
            () => OpenExternal("https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare"));
        methods.Children.Add(cloudflare);

        var standalone = HostingMethod(
            "Windows / Linux / macOS 自行部署",
            "下载对应系统的服务端包并运行。浏览器打开 http://服务器地址:24876，首次进入时完成密码设置。局域网直接使用 IP 即可；放到公网时，请用 Caddy 或 Nginx 配置 HTTPS，并放行 24876/TCP 或反向代理端口。",
            "查看服务端说明",
            () => OpenExternal("https://github.com/Laz22y/LazyForza.RaceServer"));
        Grid.SetColumn(standalone, 2);
        methods.Children.Add(standalone);
        panel.Children.Add(methods);
        panel.Children.Add(Label(
            "房主把网页总控地址留给自己，把服务端域名或 IP、房间密码发给车手。总控密码不要与房间密码相同，也不要发给参赛车手。",
            12, FontWeights.SemiBold, "WarningBrush"));
        return Card(panel);
    }

    private static Border HostingMethod(
        string title,
        string description,
        string buttonText,
        Action action)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title, 16, FontWeights.SemiBold));
        var detail = Label(description, 12, FontWeights.Normal, "MutedBrush");
        detail.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(detail);
        var button = new Button
        {
            Content = buttonText,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(14, 7, 14, 7)
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return new Border
        {
            Background = Brush("InputBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = panel
        };
    }

    private static void OpenExternal(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Border BuildEstateRaceHudSettingsEntry()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("地产赛事 HUD", 18, FontWeights.SemiBold));
        panel.Children.Add(Label(
            "排行榜、赛道一览、抓地提示、赛事横幅和五盏起跑灯已经接入统一的 Overlay 设置。五个部件使用独立于主仪表盘的全屏画布，可以分别开关、拖动、缩放和调整透明度。",
            12, FontWeights.Normal, "MutedBrush"));
        var openSettings = new Button
        {
            Content = "前往 HUD 设置",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(16, 8, 16, 8)
        };
        openSettings.Click += (_, _) => navigation.SelectedIndex = 8;
        panel.Children.Add(openSettings);
        return Card(panel);
    }

    private static Border RaceParticipantRow(
        EstateRaceParticipant participant,
        Guid? localParticipantId,
        EstateRaceSession session,
        bool allowTeams)
    {
        var phase = session.Phase;
        var qualifyingPhase = phase is RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Grid ||
                              phase == RaceSessionPhase.Suspended &&
                              session.SuspendedFromPhase == RaceSessionPhase.Practice ||
                              phase == RaceSessionPhase.Suspended &&
                              session.SuspendedFromPhase == RaceSessionPhase.Qualifying;
        var grid = new Grid { MinHeight = 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var position = Label(participant.Position.ToString(), 18, FontWeights.Bold);
        position.HorizontalAlignment = HorizontalAlignment.Center;
        position.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(position);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Label(
            participant.DisplayName + (participant.Id == localParticipantId ? "  ·  我" : string.Empty),
            14, FontWeights.SemiBold));
        if (allowTeams && !string.IsNullOrWhiteSpace(participant.TeamName))
            identity.Children.Add(Label(participant.TeamName, 11, FontWeights.Normal, "MutedBrush"));
        Grid.SetColumn(identity, 1);
        grid.Children.Add(identity);
        var statusText = qualifyingPhase && participant.QualifyingEliminatedInSession is int eliminatedIn
            ? $"Q{eliminatedIn} 淘汰"
            : RaceParticipantStatusLabel(participant);
        var status = Label(statusText, 12, FontWeights.SemiBold,
            participant.IsInPitLane ? "WarningBrush" : "MutedBrush");
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        var racePhase = session.RaceElapsedSeconds is not null;
        var total = Label(
            racePhase
                ? $"总时 {RaceTotalTime(participant.AdjustedRaceTotalSeconds)}" +
                  (participant.TimePenaltySeconds > 0 ? $"  待执行 +{participant.TimePenaltySeconds:0.#}s" : string.Empty)
                : string.Empty,
            12,
            FontWeights.Normal,
            participant.TimePenaltySeconds > 0 ? "WarningBrush" : "MutedBrush");
        total.HorizontalAlignment = HorizontalAlignment.Right;
        total.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(total, 3);
        grid.Children.Add(total);
        var leader = session.Participants.FirstOrDefault();
        var leaderLaps = leader?.CompletedLaps ?? participant.CompletedLaps;
        var deltaText = qualifyingPhase && participant.Position != 1 && participant.BestLapSeconds is null
            ? "—"
            : EstateRaceLeaderboardFormatter.FormatLeaderComparison(participant, leader, leaderLaps);
        var result = Label(deltaText, 13, FontWeights.SemiBold,
            participant.Position == 1 ? "PurpleBrush" : null);
        result.HorizontalAlignment = HorizontalAlignment.Right;
        result.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(result, 4);
        grid.Children.Add(result);
        return new Border
        {
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = participant.Id == localParticipantId ? new SolidColorBrush(Color.FromArgb(24, 66, 215, 232)) : Brushes.Transparent,
            Child = grid
        };
    }

    private static string ConnectionBrush(EstateRaceConnectionState state) => state switch
    {
        EstateRaceConnectionState.Connected => "SuccessBrush",
        EstateRaceConnectionState.Connecting or EstateRaceConnectionState.Reconnecting => "WarningBrush",
        EstateRaceConnectionState.Rejected or EstateRaceConnectionState.Faulted => "DangerBrush",
        _ => "MutedBrush"
    };

    private static string RacePhaseLabel(RaceSessionPhase phase) => phase switch
    {
        RaceSessionPhase.Lobby => "大厅",
        RaceSessionPhase.Practice => "练习赛",
        RaceSessionPhase.Qualifying => "排位赛",
        RaceSessionPhase.Grid => "发车区",
        RaceSessionPhase.OutLap => "出场圈",
        RaceSessionPhase.FormationLap => "暖胎圈",
        RaceSessionPhase.Countdown => "五盏红灯",
        RaceSessionPhase.Race => "正赛",
        RaceSessionPhase.Suspended => "红旗暂停",
        _ => "比赛结束"
    };

    private static string RaceFlagLabel(RaceControlFlag flag) => flag switch
    {
        RaceControlFlag.Green => "绿旗",
        RaceControlFlag.Yellow => "黄旗",
        RaceControlFlag.Red => "红旗",
        _ => "方格旗"
    };

    private static string RaceTotalTime(double? seconds)
    {
        if (seconds is not double value || !double.IsFinite(value) || value < 0) return "—";
        var span = TimeSpan.FromSeconds(value);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private static string PitStrategyWindowText(EstatePitStrategyPrediction? prediction)
    {
        if (prediction is null) return "—";
        return prediction.Decision switch
        {
            EstatePitStrategyDecision.PitThisLap => "本圈末",
            EstatePitStrategyDecision.PitWindow when prediction.PitWindowStartLap is int start &&
                                                     prediction.PitWindowEndLap is int end && start == end =>
                $"第 {start} 圈末",
            EstatePitStrategyDecision.PitWindow when prediction.PitWindowStartLap is int start &&
                                                     prediction.PitWindowEndLap is int end =>
                $"第 {start}–{end} 圈末",
            EstatePitStrategyDecision.StayOut => "暂不进站",
            EstatePitStrategyDecision.InPit => "进站中",
            EstatePitStrategyDecision.Finished => "已结束",
            _ => "—"
        };
    }

    private static string PitStrategyConfidenceText(EstatePitStrategyConfidence? confidence) => confidence switch
    {
        EstatePitStrategyConfidence.High => "高置信度",
        EstatePitStrategyConfidence.Medium => "中置信度",
        _ => "低置信度"
    };

    private static string PracticeTestStatusText(EstatePracticeTestStatus status) => status switch
    {
        EstatePracticeTestStatus.Active => "进行中",
        EstatePracticeTestStatus.Completed => "已完成",
        EstatePracticeTestStatus.Failed => "未通过",
        EstatePracticeTestStatus.Cancelled => "已结束",
        _ => "可开始"
    };

    private sealed record EstatePracticeTestControls(
        TextBlock Title,
        TextBlock Status,
        TextBlock Description,
        TextBlock Guidance,
        ProgressBar Progress,
        Button Action);

    private static string RaceParticipantStatusLabel(EstateRaceParticipant participant)
    {
        if (!participant.IsConnected) return "已掉线";
        if (participant.IsInServiceZone)
        {
            if (participant.IsServingTimePenalty)
                return $"执行罚时 {participant.PenaltyServiceElapsedSeconds:0.0}/{participant.PenaltyServiceRequiredSeconds:0.#} 秒";
            if (participant.PenaltyServiceCompleted)
                return "罚时已完成，可以开始换胎";
            return participant.PitServiceRequirementMet
                ? $"维修停留完成 · {participant.CompletedPitServices} 次"
                : participant.PitServiceElapsedSeconds > 0
                    ? $"维修停留 {participant.PitServiceElapsedSeconds:0.0} 秒"
                    : "正在维修区服务";
        }
        if (participant.IsInPitLane) return "维修区通道";
        return participant.Status switch
        {
            RaceParticipantStatus.Ready => "已准备",
            RaceParticipantStatus.Finished => "已完赛",
            RaceParticipantStatus.DidNotFinish => "退赛",
            RaceParticipantStatus.Disqualified => "取消资格",
            _ => "赛道上"
        };
    }
}
