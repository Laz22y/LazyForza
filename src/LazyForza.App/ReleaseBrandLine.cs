using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LazyForza.App;

/// <summary>One fixed-baseline line under the logo; no layout animation and no background timer while paused.</summary>
internal sealed class ReleaseBrandLine : Control
{
    private readonly ReleaseLabelMotion motion = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly string version;
    private string releaseName = "";
    private string combined = "";
    private FormattedText[]? text;
    private FormattedText? trimmed;
    private Brush? measuredBrush;
    private double measuredDpi;
    private Window? owner;
    private long lastTick;
    private bool reduceMotion;
    internal bool IsAnimationScheduled => timer.IsEnabled;
    internal ReleaseLabelMode DisplayMode => motion.Mode;

    public ReleaseBrandLine(string version, string releaseName)
    {
        this.version = version;
        Height = 20;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 12;
        Foreground = new SolidColorBrush(Color.FromRgb(154, 164, 178));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = false;
        SetReleaseName(releaseName);
        timer.Tick += (_, _) => { AdvanceToNow(); InvalidateVisual(); Schedule(); };
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            timer.Stop();
            if (owner is not null)
            {
                owner.Activated -= PlaybackChanged;
                owner.Deactivated -= PlaybackChanged;
                owner.StateChanged -= PlaybackChanged;
                owner = null;
            }
        };
        MouseEnter += (_, _) => PlaybackChanged(this, EventArgs.Empty);
        MouseLeave += (_, _) => PlaybackChanged(this, EventArgs.Empty);
        IsVisibleChanged += (_, _) => PlaybackChanged(this, EventArgs.Empty);
        SizeChanged += (_, _) => { Reconfigure(); PlaybackChanged(this, EventArgs.Empty); };
    }

    public void SetReleaseName(string value)
    {
        if (releaseName == value && combined.Length != 0) return;
        releaseName = value.Trim();
        combined = releaseName.Length == 0 ? version : $"{version} · {releaseName}";
        ToolTip = releaseName.Length == 0 ? version : $"{version}\n{releaseName}";
        ToolTipService.SetInitialShowDelay(this, 200);
        ToolTipService.SetShowDuration(this, 60000);
        AutomationProperties.SetName(this, combined);
        timer.Stop();
        text = null;
        motion.Reset();
        Reconfigure();
        PlaybackChanged(this, EventArgs.Empty);
    }

    public void SetReduceMotion(bool value)
    {
        if (reduceMotion == value) return;
        reduceMotion = value;
        Reconfigure();
        PlaybackChanged(this, EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size constraint) => new(0, Height);
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (combined.Length == 0 || args.Property != ForegroundProperty && args.Property != FontFamilyProperty && args.Property != FontSizeProperty) return;
        text = null;
        Reconfigure();
        PlaybackChanged(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0) return;
        EnsureMetrics();
        var frame = motion.Frame;
        var current = frame.Item < 0 ? trimmed! : text![frame.Item];
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        drawingContext.PushOpacity(frame.Opacity);
        // All strings share a baseline even when English/Chinese fallback glyphs have different ascenders.
        drawingContext.DrawText(current, new Point(frame.Offset, 15 - current.Baseline));
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        text = null;
        Reconfigure();
        PlaybackChanged(this, EventArgs.Empty);
    }

    private void EnsureMetrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (text is not null && ReferenceEquals(measuredBrush, Foreground) && measuredDpi == dpi) return;
        measuredBrush = Foreground;
        measuredDpi = dpi;
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Format(string value) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, FontSize, Foreground, dpi);
        text = [Format(version), Format(releaseName), Format(combined)];
        trimmed = Format(combined);
        ConfigureMetrics();
    }

    private void Reconfigure()
    {
        if (timer.IsEnabled) AdvanceToNow();
        timer.Stop();
        lastTick = Stopwatch.GetTimestamp();
        EnsureMetrics();
        ConfigureMetrics();
        InvalidateVisual();
    }

    private void ConfigureMetrics()
    {
        if (text is null || trimmed is null) return;
        trimmed.MaxTextWidth = Math.Max(1, ActualWidth);
        trimmed.MaxLineCount = 1;
        trimmed.Trimming = TextTrimming.CharacterEllipsis;
        motion.Configure(ActualWidth, text[0].WidthIncludingTrailingWhitespace, text[1].WidthIncludingTrailingWhitespace,
            text[2].WidthIncludingTrailingWhitespace, releaseName.Length != 0, reduceMotion);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        owner = Window.GetWindow(this);
        if (owner is not null)
        {
            owner.Activated += PlaybackChanged;
            owner.Deactivated += PlaybackChanged;
            owner.StateChanged += PlaybackChanged;
        }
        Reconfigure();
        PlaybackChanged(this, EventArgs.Empty);
    }

    private void PlaybackChanged(object? sender, EventArgs args)
    {
        if (timer.IsEnabled) AdvanceToNow();
        timer.Stop();
        lastTick = Stopwatch.GetTimestamp();
        InvalidateVisual();
        Schedule();
    }

    private void AdvanceToNow()
    {
        var now = Stopwatch.GetTimestamp();
        motion.Advance(Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds);
        lastTick = now;
    }

    private void Schedule()
    {
        timer.Stop();
        var delay = motion.Frame.NextUpdateSeconds;
        if (!IsLoaded || !IsVisible || IsMouseOver || owner is { IsActive: false } ||
            owner?.WindowState == WindowState.Minimized || !double.IsFinite(delay)) return;
        timer.Interval = TimeSpan.FromSeconds(Math.Max(1d / 30, delay));
        timer.Start();
    }
}
