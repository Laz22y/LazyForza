using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LazyForza.App;

/// <summary>One fixed-baseline brand line; no layout animation and no background timer while paused.</summary>
internal sealed class ReleaseBrandLine : Control
{
    private readonly ReleaseLabelMotion motion;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly string version;
    private string releaseName = "";
    private string combined = "";
    private FormattedText[]? text;
    private DrawingGroup[]? textDrawings;
    private FormattedText? trimmed;
    private readonly LinearGradientBrush edgeMask = new()
    {
        MappingMode = BrushMappingMode.Absolute,
        GradientStops = [new(Colors.White, 0), new(Colors.White, .1), new(Colors.White, .9), new(Colors.White, 1)]
    };
    private Brush? measuredBrush;
    private double measuredDpi;
    private Window? owner;
    private long lastTick;
    private bool reduceMotion;
    private bool rendering;
    private TimeSpan? lastRenderingTime;
    internal bool IsAnimationScheduled => timer.IsEnabled || rendering;
    internal bool IsRenderingSubscribed => rendering;
    internal ReleaseLabelMode DisplayMode => motion.Mode;
    private bool CanAnimate => IsLoaded && IsVisible && !IsMouseOver && owner is not { IsActive: false } &&
        owner?.WindowState != WindowState.Minimized;

    public ReleaseBrandLine(string version, string releaseName, ReleaseLabelMotion? motion = null)
    {
        this.motion = motion ?? new();
        this.version = version;
        Height = 20;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 12;
        Foreground = new SolidColorBrush(Color.FromRgb(154, 164, 178));
        UseLayoutRounding = true;
        // Fractional horizontal movement must not snap glyphs to whole pixels on each frame.
        SnapsToDevicePixels = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        ClipToBounds = true;
        Focusable = false;
        SetReleaseName(releaseName);
        timer.Tick += (_, _) =>
        {
            if (!timer.IsEnabled || !CanAnimate) { StopScheduling(); return; }
            AdvanceToNow();
            InvalidateVisual();
            Schedule();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            StopScheduling();
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
        StopScheduling();
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
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        if (frame.Item < 0)
            drawingContext.DrawText(trimmed!, new Point(0, 15 - trimmed!.Baseline));
        else
            DrawItem(drawingContext, frame.Item, frame.Offset, frame.Opacity);
        drawingContext.Pop();
    }

    private void DrawItem(DrawingContext context, int item, double offset, double opacity)
    {
        if (opacity <= 0) return;
        var overflow = Math.Max(0, text![item].WidthIncludingTrailingWhitespace - ActualWidth);
        context.PushOpacity(opacity);
        if (overflow > 0)
        {
            var feather = Math.Min(8, ActualWidth / 4);
            // Only cropped edges fade. The first and last characters become fully visible at rest.
            edgeMask.StartPoint = new Point(0, 0);
            edgeMask.EndPoint = new Point(ActualWidth, 0);
            edgeMask.GradientStops[0].Color = Color.FromScRgb((float)(1 - Math.Clamp(-offset / feather, 0, 1)), 1, 1, 1);
            edgeMask.GradientStops[1].Offset = feather / ActualWidth;
            edgeMask.GradientStops[2].Offset = 1 - feather / ActualWidth;
            edgeMask.GradientStops[3].Color = Color.FromScRgb((float)(1 - Math.Clamp((overflow + offset) / feather, 0, 1)), 1, 1, 1);
            context.PushOpacityMask(edgeMask);
            context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            // Anchor the mask to the viewport, including when the text translates beyond its left edge.
            context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        }
        context.PushTransform(new TranslateTransform(offset, 0));
        context.DrawDrawing(textDrawings![item]);
        context.Pop();
        if (overflow > 0) { context.Pop(); context.Pop(); }
        context.Pop();
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
        textDrawings = text.Take(2).Select(formatted =>
        {
            var drawing = new DrawingGroup();
            using var context = drawing.Open();
            // Cache glyphs on a shared baseline; animation transforms the drawing instead of reformatting text.
            context.DrawText(formatted, new Point(0, 15 - formatted.Baseline));
            return drawing;
        }).ToArray();
        trimmed = Format(combined);
        ConfigureMetrics();
    }

    private void Reconfigure()
    {
        if (IsAnimationScheduled) AdvanceToNow();
        StopScheduling();
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
        if (IsAnimationScheduled) AdvanceToNow();
        StopScheduling();
        lastTick = Stopwatch.GetTimestamp();
        InvalidateVisual();
        Schedule();
    }

    private void AdvanceToNow()
    {
        var now = Stopwatch.GetTimestamp();
        // A blocked dispatcher must not jump over an entire transition or race through hidden text.
        var budget = rendering ? .05 : motion.Frame.NextUpdateSeconds + .05;
        motion.Advance(Math.Min(Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds, budget));
        lastTick = now;
    }

    private void Schedule()
    {
        timer.Stop();
        var delay = motion.Frame.NextUpdateSeconds;
        if (!CanAnimate || !double.IsFinite(delay))
        {
            StopScheduling();
            return;
        }
        if (delay <= 0)
        {
            if (rendering) return;
            rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            StopScheduling();
            timer.Interval = TimeSpan.FromSeconds(Math.Max(.001, delay));
            timer.Start();
        }
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        // A callback already queued when focus was lost must not advance the paused presentation.
        if (!rendering || !CanAnimate) { StopScheduling(); return; }
        if (args is RenderingEventArgs frame)
        {
            if (lastRenderingTime == frame.RenderingTime) return;
            lastRenderingTime = frame.RenderingTime;
        }
        AdvanceToNow();
        InvalidateVisual();
        Schedule();
    }

    private void StopScheduling()
    {
        timer.Stop();
        if (rendering) CompositionTarget.Rendering -= OnRendering;
        rendering = false;
        lastRenderingTime = null;
    }
}
