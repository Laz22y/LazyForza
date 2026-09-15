using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Text.Json;
using LazyForza.Domain;
using LazyForza.Overlay;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private sealed record HudThemeChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private UIElement BuildHudSettings()
    {
        var current = overlay.CurrentLayout;
        var currentDashboardBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Dashboard);
        var currentLapBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Lap);
        var currentDriftBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Drift);
        var currentDashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
            current.DashboardWidgets);
        var currentEstateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
            current.EstateRaceWidgets);
        var dashboardWidgetKinds = Enum.GetValues<DashboardWidgetKind>();
        var estateRaceWidgetKinds = Enum.GetValues<EstateRaceHudWidgetKind>();
        var visibleDashboardWidgetCount = dashboardWidgetKinds
            .Count(kind => currentDashboardWidgets.Get(kind).IsVisible);
        var visibleEstateRaceWidgetCount = estateRaceWidgetKinds
            .Count(kind => currentEstateRaceWidgets.Get(kind).IsVisible);
        var controls = new StackPanel();
        var hudAppearance = new StackPanel();
        var hudComponents = new StackPanel();
        var hudOpacity = new StackPanel();
        var hudTiming = new StackPanel();

        var overlayHeader = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        overlayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new StackPanel();
        headerText.Children.Add(Label("Overlay 设置", 17, FontWeights.SemiBold));
        var overlaySummary = Label(
            AppLocalization.Format(
                "settings.overlay.summary",
                "仪表盘 {0:0} × {1:0} · 圈速 {2:0} × {3:0} · 漂移 {4:0} × {5:0} · 仪表盘部件 {6}/{7} · 赛事部件 {8}/{9} · {10} · 不透明度 {11:P0} · {12}",
                currentDashboardBounds.Width,
                currentDashboardBounds.Height,
                currentLapBounds.Width,
                currentLapBounds.Height,
                currentDriftBounds.Width,
                currentDriftBounds.Height,
                visibleDashboardWidgetCount,
                dashboardWidgetKinds.Length,
                visibleEstateRaceWidgetCount,
                estateRaceWidgetKinds.Length,
                AppLocalization.Literal(current.LapHudAttachedToDashboard ? "已吸附" : "独立布局"),
                current.Opacity,
                current.MonitorId),
            11, FontWeights.Normal, "MutedBrush");
        overlaySummary.Margin = new Thickness(0, 3, 0, 0);
        headerText.Children.Add(overlaySummary);
        var interactionNote = Label(
            "运行时始终点击穿透且不可移动；位置与大小只在布局编辑器中调整。",
            10,
            FontWeights.Normal,
            "MutedBrush");
        interactionNote.Margin = new Thickness(0, 3, 0, 0);
        headerText.Children.Add(interactionNote);
        overlayHeader.Children.Add(headerText);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        var editLayout = new Button
        {
            Content = "设置 Overlay 布局",
            Padding = new Thickness(13, 7, 13, 7),
            ToolTip = "以 Forza Horizon 6 当前窗口为背景，拖动并缩放 Overlay"
        };
        editLayout.Click += async (_, _) =>
            await ConfigureOverlayLayoutAsync(editLayout);
        headerActions.Children.Add(editLayout);
        var resetDefaults = new Button
        {
            Content = "重置 Overlay",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(12, 7, 12, 7),
            ToolTip = "恢复 HUD 的默认布局与显示设置。"
        };
        resetDefaults.Click += async (_, _) =>
        {
            if (AppDialog.Show(
                    AppLocalization.Literal("确定重置 Overlay 设置吗？\n\n位置、尺寸、仪表盘部件、地产赛事部件、透明度、动态和时间参数将恢复默认值。监听 IP、UDP 端口与本地数据不受影响。"),
                    AppLocalization.Literal("重置 Overlay"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var defaultLayout = LazyForzaDefaults.CreateOverlayLayout();
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(defaultLayout));
            await overlay.SetLayoutAsync(defaultLayout, CancellationToken.None);
            RenderSelectedPage();
        };
        headerActions.Children.Add(resetDefaults);
        Grid.SetColumn(headerActions, 1);
        overlayHeader.Children.Add(headerActions);
        hudAppearance.Children.Add(overlayHeader);

        var primarySettings = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var appearance = new StackPanel();
        var dashboardScale = AddValueSlider(
            appearance, "仪表盘 HUD 精确缩放", "按 1% 调整，并保持仪表盘中心点不变", current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        dashboardScale.SmallChange = OverlayScaleSettings.Step;
        dashboardScale.LargeChange = 0.05;
        var lapScale = AddValueSlider(
            appearance, "圈速 HUD 精确缩放", "按 1% 调整，并保持圈速 HUD 中心点不变",
            current.LapHudScale ?? current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        lapScale.SmallChange = OverlayScaleSettings.Step;
        lapScale.LargeChange = 0.05;
        var driftScale = AddValueSlider(
            appearance,
            "漂移 HUD 精确缩放 · 实验性",
            "按 1% 调整，并保持漂移 HUD 中心点不变",
            current.DriftHudScale ?? current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        driftScale.SmallChange = OverlayScaleSettings.Step;
        driftScale.LargeChange = 0.05;
        var opacityControls = new StackPanel();
        var opacity = AddValueSlider(
            opacityControls, "整体不透明度", "同时调整所有 HUD 的文字与底板；会与部件不透明度叠加。", current.Opacity,
            0.25, 1, 0.05, value => value.ToString("P0"));
        var estateBackdropOpacity = AddValueSlider(
            opacityControls, "地产赛事底板不透明度", "只调整赛事面板底色，文字、旗语和状态标识保持清晰。",
            current.EstateRaceBackdropOpacity, 0, 1, 0.05, value => value.ToString("P0"));
        var monitor = Label(
            AppLocalization.Format("settings.overlay.monitor", "当前显示器：{0}", current.MonitorId),
            10,
            FontWeights.Normal,
            "MutedBrush");
        monitor.Margin = new Thickness(0, 0, 0, 4);
        appearance.Children.Add(monitor);
        primarySettings.Children.Add(SettingGroup(
            "外观",
            "分别调整三个主 HUD；地产赛事部件在可视化布局编辑器中独立缩放。",
            appearance));

        var interaction = new StackPanel();
        var toggleRow = new WrapPanel { Margin = new Thickness(-4, -2, 0, 8) };
        var reduceMotion = new ToggleButton
        {
            Content = current.ReduceMotion ? "减少动态：开" : "减少动态：关",
            IsChecked = current.ReduceMotion
        };
        var dashboardMotion = new ToggleButton
        {
            Content = current.DashboardMotionEnabled ? "加速度跟随：开" : "加速度跟随：关",
            IsChecked = current.DashboardMotionEnabled,
            ToolTip = "让仪表盘随车辆加速度轻微移动"
        };
        toggleRow.Children.Add(reduceMotion);
        toggleRow.Children.Add(dashboardMotion);
        interaction.Children.Add(toggleRow);
        var motionIntensity = AddValueSlider(
            interaction, "动态强度", "控制加速度跟随的位移幅度",
            current.DashboardMotionIntensity, 0, 1, 0.05, value => value.ToString("P0"));

        void RefreshInteractionControls()
        {
            reduceMotion.Content = AppLocalization.Literal(
                reduceMotion.IsChecked == true ? "减少动态：开" : "减少动态：关");
            dashboardMotion.Content = AppLocalization.Literal(
                dashboardMotion.IsChecked == true ? "加速度跟随：开" : "加速度跟随：关");
            motionIntensity.IsEnabled = dashboardMotion.IsChecked == true && reduceMotion.IsChecked != true;
        }

        reduceMotion.Click += (_, _) => RefreshInteractionControls();
        dashboardMotion.Click += (_, _) =>
        {
            RefreshInteractionControls();
        };
        RefreshInteractionControls();
        syncHudMotionPreference = () =>
        {
            reduceMotion.IsChecked = overlay.TimingLayout.ReduceMotion;
            RefreshInteractionControls();
        };
        interaction.Children.Add(opacityControls);
        var interactionGroup = SettingGroup(
            "透明度与动态",
            "调节整体可见度、赛事底板与动态效果。",
            interaction);
        Grid.SetColumn(interactionGroup, 2);
        primarySettings.Children.Add(interactionGroup);
        hudAppearance.Children.Add(primarySettings);

        var dashboardComponentItems = new[]
        {
            (DashboardWidgetKind.RpmArc, "转速灯带"),
            (DashboardWidgetKind.SpeedGear, "速度 / 挡位"),
            (DashboardWidgetKind.EngineOutput, "转速 / 动力"),
            (DashboardWidgetKind.Tires, "轮胎状态"),
            (DashboardWidgetKind.Pedals, "油门 / 制动"),
            (DashboardWidgetKind.Steering, "方向指示"),
            (DashboardWidgetKind.ClassBadge, "等级 / PI")
        };
        var componentToggles = new Dictionary<DashboardWidgetKind, ToggleButton>();
        var componentPanel = new WrapPanel { Margin = new Thickness(-4, -2, 0, 0) };
        foreach (var (kind, name) in dashboardComponentItems)
        {
            var toggle = new ToggleButton
            {
                IsChecked = currentDashboardWidgets.Get(kind).IsVisible,
                Margin = new Thickness(4, 2, 4, 6),
                Padding = new Thickness(10, 6, 10, 6),
                MinWidth = 106
            };
            void RefreshComponentToggle() => toggle.Content = AppLocalization.Format(
                "settings.hud.componentToggle",
                "{0}：{1}",
                AppLocalization.Literal(name),
                AppLocalization.Literal(toggle.IsChecked == true ? "开" : "关"));
            toggle.Click += (_, _) => RefreshComponentToggle();
            RefreshComponentToggle();
            componentToggles[kind] = toggle;
            componentPanel.Children.Add(toggle);
        }
        hudComponents.Children.Add(SettingGroup(
            "主仪表盘部件",
            "各部件可独立开关；位置请在 Overlay 布局编辑器中拖动，并可一键恢复当前默认布局。",
            componentPanel));

        var estateRaceComponentItems = new[]
        {
            (EstateRaceHudWidgetKind.Leaderboard, "比赛排行榜"),
            (EstateRaceHudWidgetKind.TrackMap, "赛道一览"),
            (EstateRaceHudWidgetKind.GripStatus, "抓地提示"),
            (EstateRaceHudWidgetKind.Banner, "赛事横幅"),
            (EstateRaceHudWidgetKind.StartLights, "五盏红灯"),
            (EstateRaceHudWidgetKind.PitStopInfo, "维修站信息"),
            (EstateRaceHudWidgetKind.PitLimiter, "维修区限速"),
            (EstateRaceHudWidgetKind.PenaltyStatus, "罚时指示器"),
            (EstateRaceHudWidgetKind.PracticeProgram, "练习项目提示"),
            (EstateRaceHudWidgetKind.PitWindowSuggestion, "进站窗口建议"),
            (EstateRaceHudWidgetKind.FullRaceStrategy, "整场进站策略")
        };
        var estateRaceToggles = new Dictionary<EstateRaceHudWidgetKind, ToggleButton>();
        var estateRaceOpacitySliders = new Dictionary<EstateRaceHudWidgetKind, Slider>();
        var estateRaceThemes = new Dictionary<EstateRaceHudWidgetKind, ComboBox>();
        var showThemeChoices = EstateRaceHudThemes.Definitions.Count > 1 || estateRaceWidgetKinds.Any(kind =>
            !string.Equals(currentEstateRaceWidgets.Get(kind).ThemeId, EstateRaceHudThemeIds.Classic, StringComparison.OrdinalIgnoreCase));
        var themeControls = new StackPanel();
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 12),
            Visibility = showThemeChoices ? Visibility.Visible : Visibility.Collapsed };
        foreach (var theme in EstateRaceHudThemes.Definitions)
        {
            var preset = new Button
            {
                Content = AppLocalization.Format("settings.hud.allTheme", "全部使用{0}", AppLocalization.Literal(theme.Name)),
                FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            preset.Click += (_, _) =>
            {
                foreach (var selector in estateRaceThemes.Values) selector.SelectedValue = theme.Id;
            };
            presets.Children.Add(preset);
        }
        themeControls.Children.Add(presets);
        Grid ComponentRow()
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = showThemeChoices ? new GridLength(1.25, GridUnitType.Star) : new GridLength(0) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            return row;
        }
        var tableHeader = ComponentRow();
        var titles = new[] { "组件", "主题", "显示", "不透明度" };
        for (var column = 0; column < titles.Length; column++)
        {
            var label = Label(titles[column], 11, FontWeights.Normal, "MutedBrush");
            label.Margin = new Thickness(4, 0, 0, 4);
            if (column == 1 && !showThemeChoices) label.Visibility = Visibility.Collapsed;
            Grid.SetColumn(label, column);
            tableHeader.Children.Add(label);
        }
        themeControls.Children.Add(tableHeader);
        foreach (var (kind, name) in estateRaceComponentItems)
        {
            var placement = currentEstateRaceWidgets.Get(kind);
            var row = ComponentRow();
            var nameLabel = Label(name, 12, FontWeights.Normal);
            nameLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            nameLabel.ToolTip = AppLocalization.Literal(name);
            nameLabel.VerticalAlignment = VerticalAlignment.Center;
            nameLabel.Margin = new Thickness(4, 0, 8, 0);
            row.Children.Add(nameLabel);
            var choices = EstateRaceHudThemes.Definitions.Select(theme => new HudThemeChoice(
                theme.Id, AppLocalization.Literal(theme.Name))).ToList();
            if (!choices.Any(choice => string.Equals(choice.Id, placement.ThemeId, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new HudThemeChoice(placement.ThemeId,
                    AppLocalization.Format("settings.hud.unavailableTheme", "{0}（暂用经典）", placement.ThemeId)));
            var selector = new ComboBox
            {
                ItemsSource = choices, DisplayMemberPath = nameof(HudThemeChoice.Name),
                SelectedValuePath = nameof(HudThemeChoice.Id),
                SelectedValue = choices.First(choice => string.Equals(choice.Id, placement.ThemeId, StringComparison.OrdinalIgnoreCase)).Id,
                FontSize = 12, MinWidth = 0, MinHeight = 34, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Visibility = showThemeChoices ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = AppLocalization.Literal("只改变样式，保留组件的位置、缩放和透明度。")
            };
            AutomationProperties.SetName(selector, AppLocalization.Literal(name) + " · " + AppLocalization.Literal("主题"));
            estateRaceThemes[kind] = selector;
            Grid.SetColumn(selector, 1);
            row.Children.Add(selector);
            var toggle = new ToggleButton
            {
                IsChecked = placement.IsVisible, Margin = new Thickness(0, 0, 12, 0),
                FontSize = 12, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center
            };
            void RefreshToggle() => toggle.Content = AppLocalization.Literal(toggle.IsChecked == true ? "开" : "关");
            toggle.Click += (_, _) => RefreshToggle();
            RefreshToggle();
            AutomationProperties.SetName(toggle, AppLocalization.Literal(name));
            estateRaceToggles[kind] = toggle;
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
            var opacityCell = new Grid();
            opacityCell.ColumnDefinitions.Add(new ColumnDefinition());
            opacityCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            var value = Label(placement.Opacity.ToString("P0"), 11, FontWeights.Normal, "MutedBrush");
            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right;
            var slider = new Slider
            {
                Minimum = 0.15, Maximum = 1, Value = placement.Opacity,
                TickFrequency = 0.05, SmallChange = 0.05, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString("P0");
            AutomationProperties.SetName(slider, AppLocalization.Literal(name) + " · " + AppLocalization.Literal("不透明度"));
            estateRaceOpacitySliders[kind] = slider;
            opacityCell.Children.Add(slider);
            Grid.SetColumn(value, 1);
            opacityCell.Children.Add(value);
            Grid.SetColumn(opacityCell, 3);
            row.Children.Add(opacityCell);
            themeControls.Children.Add(row);
        }
        var themeNote = Label("每个组件可独立选择主题。", 12, FontWeights.Normal, "MutedBrush");
        themeNote.Visibility = showThemeChoices ? Visibility.Visible : Visibility.Collapsed;
        themeNote.Margin = new Thickness(0, 0, 0, 14);
        themeControls.Children.Insert(0, themeNote);
        hudOpacity.Children.Add(themeControls);

        var timingItems = new UniformGrid { Columns = 2 };
        var dashboardIdleWait = AddTimeSlider(timingItems, "仪表盘静止等待", current.DashboardIdleWaitSeconds, 0, 15, 0.5);
        var dashboardFade = AddTimeSlider(timingItems, "仪表盘淡入 / 淡出", current.DashboardVisibilityFadeSeconds, 0.1, 3, 0.1);
        var completedLapHold = AddTimeSlider(timingItems, "完成圈分段保留", current.LapCompletedHoldSeconds, 0, 10, 0.5);
        var noMatchConfirmation = AddTimeSlider(timingItems, "无匹配赛道确认", current.LapNoMatchConfirmationSeconds, 1, 30, 0.5);
        var noMatchFade = AddTimeSlider(timingItems, "无匹配圈速 HUD 淡出", current.LapNoMatchFadeSeconds, 0.1, 3, 0.1);
        var liveHudStale = AddTimeSlider(timingItems, "Live HUD 断流隐藏", current.LiveHudStaleSeconds, 0.1, 3, 0.1);
        hudTiming.Children.Add(SettingGroup(
            "HUD 时间",
            "调整 HUD 的等待、保留和淡入淡出时间。",
            timingItems));

        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.appearance", "外观与动态"),
            AppLocalization.Text(
                "settings.hud.appearanceDetail",
                "布局、缩放、不透明度和动态效果。"),
            hudAppearance,
            hudAppearanceExpanded,
            expanded => hudAppearanceExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.components", "部件显示"),
            AppLocalization.Text(
                "settings.hud.componentsDetail",
                "管理仪表盘的显示内容。"),
            hudComponents,
            hudComponentsExpanded,
            expanded => hudComponentsExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            showThemeChoices ? AppLocalization.Text("settings.hud.themes", "赛事主题与组件")
                : AppLocalization.Text("settings.hud.raceWidgets", "赛事组件"),
            showThemeChoices ? AppLocalization.Text("settings.hud.themesDetail", "逐项选择主题、显示开关和不透明度。")
                : AppLocalization.Text("settings.hud.raceWidgetsDetail", "逐项调整显示开关和不透明度。"),
            hudOpacity,
            hudOpacityExpanded,
            expanded => hudOpacityExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.timing", "显示时序"),
            AppLocalization.Text(
                "settings.hud.timingDetail",
                "调整 HUD 的等待、保留和淡入淡出时间。"),
            hudTiming,
            hudTimingExpanded,
            expanded => hudTimingExpanded = expanded));

        var applyStatus = AutoApplyStatus();
        controls.Children.Insert(0, applyStatus);
        var automatic = AutoApplySettings(controls, async () =>
        {
            var dashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
                overlay.CurrentLayout.DashboardWidgets);
            foreach (var (kind, toggle) in componentToggles)
            {
                var placement = dashboardWidgets.Get(kind);
                dashboardWidgets = dashboardWidgets.Set(
                    kind,
                    placement with { IsVisible = toggle.IsChecked == true });
            }
            var estateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
                overlay.CurrentLayout.EstateRaceWidgets);
            foreach (var (kind, toggle) in estateRaceToggles)
            {
                var placement = estateRaceWidgets.Get(kind);
                estateRaceWidgets = estateRaceWidgets.Set(
                    kind,
                    placement with
                    {
                        IsVisible = toggle.IsChecked == true,
                        Opacity = estateRaceOpacitySliders[kind].Value,
                        ThemeId = estateRaceThemes[kind].SelectedValue as string ?? placement.ThemeId
                    });
            }
            var next = overlay.CurrentLayout with
            {
                Opacity = opacity.Value,
                EstateRaceBackdropOpacity = estateBackdropOpacity.Value,
                ClickThrough = true,
                IsLocked = true,
                ReduceMotion = reduceMotion.IsChecked == true,
                DashboardMotionEnabled = dashboardMotion.IsChecked == true,
                DashboardMotionIntensity = motionIntensity.Value,
                DashboardIdleWaitSeconds = dashboardIdleWait.Value,
                DashboardVisibilityFadeSeconds = dashboardFade.Value,
                LapCompletedHoldSeconds = completedLapHold.Value,
                LapNoMatchConfirmationSeconds = noMatchConfirmation.Value,
                LapNoMatchFadeSeconds = noMatchFade.Value,
                LiveHudStaleSeconds = liveHudStale.Value,
                DashboardWidgets = dashboardWidgets,
                EstateRaceWidgets = estateRaceWidgets
            };
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Dashboard,
                dashboardScale.Value);
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Lap,
                lapScale.Value);
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Drift,
                driftScale.Value);
            if (overlay.CurrentLayout.LapHudAttachedToDashboard &&
                Math.Abs(dashboardScale.Value - lapScale.Value) <
                OverlayScaleSettings.Step / 2)
                next = OverlayLayoutGeometry.AttachLapToDashboard(next);
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
            await overlay.SetLayoutAsync(next, CancellationToken.None);
            applyStatus.Visibility = Visibility.Collapsed;
            RefreshSidebar();
        }, applyStatus);
        controls.AddHandler(Slider.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => automatic.Request()));
        foreach (var toggle in componentToggles.Values.Concat(estateRaceToggles.Values).Append(reduceMotion).Append(dashboardMotion))
        {
            toggle.Checked += (_, _) => automatic.Request();
            toggle.Unchecked += (_, _) => automatic.Request();
        }
        foreach (var theme in estateRaceThemes.Values) theme.SelectionChanged += (_, _) => automatic.Request();
        return controls;

        Slider AddValueSlider(
            Panel parent,
            string title,
            string description,
            double value,
            double minimum,
            double maximum,
            double tick,
            Func<double, string> format)
        {
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(Label(title, 12, FontWeights.SemiBold));
            var valueLabel = Label(format(Math.Clamp(value, minimum, maximum)), 12, FontWeights.SemiBold, "AccentBrush");
            valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(valueLabel, 1);
            heading.Children.Add(valueLabel);
            container.Children.Add(heading);
            if (!string.IsNullOrWhiteSpace(description))
            {
                var help = Label(description, 10, FontWeights.Normal, "MutedBrush");
                help.Margin = new Thickness(0, 2, 0, 5);
                container.Children.Add(help);
            }
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = tick,
                IsSnapToTickEnabled = true,
                Value = Math.Clamp(value, minimum, maximum),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(slider, AppLocalization.Literal(title));
            AutomationProperties.SetHelpText(slider, AppLocalization.Literal(description));
            slider.ValueChanged += (_, _) => valueLabel.Text = format(slider.Value);
            container.Children.Add(slider);
            parent.Children.Add(container);
            return slider;
        }

        Slider AddTimeSlider(
            Panel parent,
            string title,
            double value,
            double minimum,
            double maximum,
            double tick)
        {
            var item = new StackPanel { Margin = new Thickness(6, 3, 14, 12) };
            var slider = AddValueSlider(
                item, title, "", value, minimum, maximum, tick,
                currentValue => AppLocalization.Format("settings.seconds", "{0:0.0} 秒", currentValue));
            parent.Children.Add(item);
            return slider;
        }

        Border SettingGroup(string title, string description, UIElement body)
        {
            var group = new StackPanel();
            group.Children.Add(Label(title, 14, FontWeights.SemiBold));
            var help = Label(description, 10, FontWeights.Normal, "MutedBrush");
            help.Margin = new Thickness(0, 3, 0, 12);
            group.Children.Add(help);
            group.Children.Add(body);
            return new Border
            {
                Background = Brush("PanelBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = group
            };
        }

    }
}
