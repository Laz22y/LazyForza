using System.Windows;
using System.Windows.Media;

namespace LazyForza.Overlay;

/// <summary>Semantic HUD layers. Only explicitly marked panel fills belong to the backdrop;
/// text, borders, flags and status indicators retain their own contrast.</summary>
internal sealed record EstateRaceDrawingLayers(DrawingGroup Backdrop, DrawingGroup Content)
{
    private static readonly DependencyProperty IsBackdropProperty = DependencyProperty.RegisterAttached(
        "IsBackdrop", typeof(bool), typeof(EstateRaceDrawingLayers), new PropertyMetadata(false));

    public static EstateRaceDrawingLayers Record(Action<DrawingContext> draw)
    {
        var source = new DrawingGroup();
        using (var dc = source.Open()) draw(dc);
        var (backdrop, content) = Separate(source);
        return new(backdrop, content);
    }

    public static void Panel(DrawingContext dc, Brush? fill, Pen? border, Rect bounds,
        double radiusX = 0, double radiusY = 0)
    {
        var background = new DrawingGroup();
        background.SetValue(IsBackdropProperty, true);
        using (var layer = background.Open())
            layer.DrawRoundedRectangle(fill, null, bounds, radiusX, radiusY);
        dc.DrawDrawing(background);
        // The outline stays readable even when the panel fill is transparent.
        if (border is not null) dc.DrawRoundedRectangle(null, border, bounds, radiusX, radiusY);
    }

    public void Draw(DrawingContext dc, double backdropOpacity,
        EstateRaceDrawingLayers? outgoing = null, double progress = 1)
    {
        // Composite nested panel fills first, then fade that result once. Scaling
        // each brush separately would make overlapping panels unexpectedly opaque.
        dc.PushOpacity(Math.Clamp(backdropOpacity, 0, 1));
        dc.DrawDrawing(Backdrop);
        dc.Pop();
        if (outgoing is not null && progress < 1)
        {
            dc.PushOpacity(1 - progress);
            dc.DrawDrawing(outgoing.Content);
            dc.Pop();
            dc.PushOpacity(progress);
            dc.DrawDrawing(Content);
            dc.Pop();
        }
        else dc.DrawDrawing(Content);
    }

    private static (DrawingGroup Backdrop, DrawingGroup Content) Separate(DrawingGroup source)
    {
        // Keep clips, row fades and transforms in both trees. Geometry and glyphs
        // are shared; splitting never clones the expensive drawing leaves.
        var backdrop = Shell(source);
        var content = Shell(source);
        foreach (var drawing in source.Children)
        {
            if (drawing is DrawingGroup group)
            {
                if ((bool)group.GetValue(IsBackdropProperty)) backdrop.Children.Add(group);
                else
                {
                    var split = Separate(group);
                    if (split.Backdrop.Children.Count > 0) backdrop.Children.Add(split.Backdrop);
                    if (split.Content.Children.Count > 0) content.Children.Add(split.Content);
                }
            }
            else content.Children.Add(drawing);
        }
        return (backdrop, content);
    }

    private static DrawingGroup Shell(DrawingGroup source) => new()
    {
        ClipGeometry = source.ClipGeometry,
        Transform = source.Transform,
        Opacity = source.Opacity,
        OpacityMask = source.OpacityMask,
        GuidelineSet = source.GuidelineSet
    };
}
