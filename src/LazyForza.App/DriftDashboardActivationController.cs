using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class DriftDashboardActivationController(
    ModuleManager modules,
    IModuleSettingsStore settings)
{
    public bool IntroductionSeen { get; private set; }
    public bool AutoCloseDashboard { get; private set; } = true;
    public bool IsDriftActive =>
        Module(DriftDashboardModule.ModuleId).Status.IsEnabled;

    public async ValueTask InitializeAsync(
        bool captureQa,
        bool captureDriftQa,
        bool captureDriftOnlyQa,
        CancellationToken cancellationToken)
    {
        IntroductionSeen = await ReadBooleanAsync(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.IntroductionSeenSettingKey,
                fallback: false,
                cancellationToken)
            .ConfigureAwait(false);
        AutoCloseDashboard = await ReadBooleanAsync(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.AutoCloseDashboardSettingKey,
                fallback: true,
                cancellationToken)
            .ConfigureAwait(false);

        var desired = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules.Modules)
        {
            desired[module.Descriptor.Id] = captureQa
                ? module.Descriptor.DefaultEnabled
                : await DesiredPreferenceAsync(module, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (captureDriftQa || captureDriftOnlyQa)
        {
            desired[DriftDashboardModule.ModuleId] = true;
            desired[DashboardModule.ModuleId] = !captureDriftOnlyQa;
        }

        if (desired.GetValueOrDefault(DriftDashboardModule.ModuleId))
        {
            desired[LapAnalysisModule.ModuleId] = false;
            if (AutoCloseDashboard &&
                !captureDriftQa &&
                !captureDriftOnlyQa)
                desired[DashboardModule.ModuleId] = false;
        }

        foreach (var module in modules.Modules)
        {
            if (desired.GetValueOrDefault(module.Descriptor.Id))
            {
                await modules.SetRuntimeEnabledAsync(
                        module.Descriptor.Id,
                        true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async ValueTask SetEnabledAsync(
        string moduleId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                moduleId,
                LapAnalysisModule.ModuleId,
                StringComparison.OrdinalIgnoreCase) &&
            enabled &&
            IsDriftActive)
        {
            throw new InvalidOperationException(
                "漂移仪表盘开启时，圈速分析会保持关闭且不会记录新的圈速。请先关闭漂移仪表盘。");
        }

        if (!string.Equals(
                moduleId,
                DriftDashboardModule.ModuleId,
                StringComparison.OrdinalIgnoreCase))
        {
            await modules.SetEnabledAsync(
                    moduleId,
                    enabled,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (enabled)
            await EnableDriftAsync(cancellationToken).ConfigureAwait(false);
        else
            await DisableDriftAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAutoCloseDashboardAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        AutoCloseDashboard = enabled;
        await settings.SetAsync(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.AutoCloseDashboardSettingKey,
                enabled.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsDriftActive) return;
        if (enabled)
        {
            await modules.SetRuntimeEnabledAsync(
                    DashboardModule.ModuleId,
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RestorePreferenceAsync(
                    DashboardModule.ModuleId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask MarkIntroductionSeenAsync(
        CancellationToken cancellationToken)
    {
        IntroductionSeen = true;
        await settings.SetAsync(
                DriftDashboardModule.ModuleId,
                DriftDashboardModule.IntroductionSeenSettingKey,
                bool.TrueString,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EnableDriftAsync(
        CancellationToken cancellationToken)
    {
        if (IsDriftActive) return;
        var lapWasRunning = Module(LapAnalysisModule.ModuleId).Status.IsEnabled;
        var dashboardWasRunning = Module(DashboardModule.ModuleId).Status.IsEnabled;
        try
        {
            await modules.SetRuntimeEnabledAsync(
                    LapAnalysisModule.ModuleId,
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (AutoCloseDashboard)
            {
                await modules.SetRuntimeEnabledAsync(
                        DashboardModule.ModuleId,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await modules.SetEnabledAsync(
                    DriftDashboardModule.ModuleId,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (lapWasRunning)
            {
                await modules.SetRuntimeEnabledAsync(
                        LapAnalysisModule.ModuleId,
                        true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            if (dashboardWasRunning)
            {
                await modules.SetRuntimeEnabledAsync(
                        DashboardModule.ModuleId,
                        true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw;
        }
    }

    private async ValueTask DisableDriftAsync(
        CancellationToken cancellationToken)
    {
        await modules.SetEnabledAsync(
                DriftDashboardModule.ModuleId,
                false,
                cancellationToken)
            .ConfigureAwait(false);
        await RestorePreferenceAsync(
                DashboardModule.ModuleId,
                cancellationToken)
            .ConfigureAwait(false);
        await RestorePreferenceAsync(
                LapAnalysisModule.ModuleId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask RestorePreferenceAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        var module = Module(moduleId);
        var desired = await DesiredPreferenceAsync(module, cancellationToken)
            .ConfigureAwait(false);
        await modules.SetRuntimeEnabledAsync(
                moduleId,
                desired,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> DesiredPreferenceAsync(
        ILazyForzaModule module,
        CancellationToken cancellationToken)
    {
        var saved = await settings.GetAsync(
                module.Descriptor.Id,
                "enabled",
                cancellationToken)
            .ConfigureAwait(false);
        return bool.TryParse(saved, out var parsed)
            ? parsed
            : module.Descriptor.DefaultEnabled;
    }

    private async ValueTask<bool> ReadBooleanAsync(
        string moduleId,
        string key,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var saved = await settings.GetAsync(
                moduleId,
                key,
                cancellationToken)
            .ConfigureAwait(false);
        return bool.TryParse(saved, out var parsed)
            ? parsed
            : fallback;
    }

    private ILazyForzaModule Module(string moduleId) =>
        modules.Modules.Single(module => string.Equals(
            module.Descriptor.Id,
            moduleId,
            StringComparison.OrdinalIgnoreCase));
}
