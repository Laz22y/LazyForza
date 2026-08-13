using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Modules.DriftDashboard;

public sealed class DriftDashboardModule : LazyForzaModuleBase, IHudContribution
{
    public const string ModuleId = "drift-dashboard";
    public const string IntroductionSeenSettingKey = "introductionSeen";
    public const string AutoCloseDashboardSettingKey = "autoCloseDashboard";
    private readonly DriftTelemetryAnalyzer analyzer = new();
    private ITelemetrySubscription? subscription;
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private DriftHudState? snapshot;
    private DriftHudState? staleSnapshotSource;
    private DriftHudState? staleSnapshot;

    public DriftDashboardModule()
        : base(new ModuleDescriptor(
            ModuleId,
            "漂移仪表盘（实验性）",
            "实验性功能：以防止 Spin 为优先，显示图形化方向/换挡建议、侧滑角和积分速度趋势；辅助能力有限，开启时暂停圈速分析。",
            [],
            null,
            null,
            true,
            DefaultEnabled: false))
    {
    }

    public string Id => "hud.drift-dashboard";
    public HudContributionKind Kind => HudContributionKind.DriftDashboard;
    public int ZIndex => 15;
    public object? Snapshot
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            if (current is null || current.IsStale ||
                DateTimeOffset.UtcNow - current.UpdatedAt <= TimeSpan.FromSeconds(0.8))
                return current;
            if (ReferenceEquals(Volatile.Read(ref staleSnapshotSource), current))
                return Volatile.Read(ref staleSnapshot);
            var stale = current with { IsStale = true };
            Volatile.Write(ref staleSnapshot, stale);
            Volatile.Write(ref staleSnapshotSource, current);
            return stale;
        }
    }

    protected override async ValueTask OnStartAsync(
        CancellationToken cancellationToken)
    {
        analyzer.Reset();
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        subscription = await Context.Telemetry
            .SubscribeAsync(ModuleId, runCancellation.Token)
            .ConfigureAwait(false);
        await Context.Hud.AttachAsync(this, cancellationToken)
            .ConfigureAwait(false);
        runTask = Task.Run(
            () => ConsumeAsync(
                subscription.Frames,
                runCancellation.Token),
            CancellationToken.None);
    }

    protected override async ValueTask OnStopAsync(
        CancellationToken cancellationToken)
    {
        runCancellation?.Cancel();
        if (subscription is not null)
            await subscription.DisposeAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Context.Hud.DetachAsync(Id, cancellationToken)
            .ConfigureAwait(false);
        subscription = null;
        runTask = null;
        runCancellation?.Dispose();
        runCancellation = null;
        analyzer.Reset();
        Volatile.Write(ref snapshot, null);
        Volatile.Write(ref staleSnapshotSource, null);
        Volatile.Write(ref staleSnapshot, null);
    }

    private async Task ConsumeAsync(
        System.Threading.Channels.ChannelReader<TelemetryFrame> frames,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in frames
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            Volatile.Write(ref snapshot, analyzer.Observe(frame));
        }
    }
}
