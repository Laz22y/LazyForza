using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class SharedControlThemeTests
{
    [TestMethod]
    public void SliderTemplatePreservesCommandsDraggingOrientationAndAccentUpdates() => EstateHudRenderingTests.Sta(() =>
    {
        var owner = new Grid { Width = 320, Height = 200, Resources = LoadTheme() };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 1, LargeChange = 10 };
        owner.Children.Add(slider);
        Layout(owner);
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        Assert.IsNotNull(track?.Thumb);
        Slider.IncreaseLarge.Execute(null, slider);
        Assert.AreEqual(60, slider.Value);
        Slider.DecreaseSmall.Execute(null, slider);
        Assert.AreEqual(59, slider.Value);
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(25, 0));
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(25, 0, false));
        Assert.IsTrue(slider.Value > 59, "The replacement thumb must still drag the production Slider.");
        var accent = new SolidColorBrush(Colors.MediumPurple);
        owner.Resources["AccentBrush"] = accent;
        Assert.AreSame(accent, track.DecreaseRepeatButton.Background);
        slider.Orientation = Orientation.Vertical;
        Layout(owner);
        Assert.AreEqual(Orientation.Vertical, track.Orientation);
        Assert.IsTrue(track.Thumb.ActualHeight > 0);
        slider.IsEnabled = false;
        Assert.IsFalse(track.Thumb.IsEnabled);
        Assert.IsFalse(track.DecreaseRepeatButton.IsEnabled);
    });

    [TestMethod]
    public void TextInputTemplatePreservesEditingSelectionAndScrolling() => EstateHudRenderingTests.Sta(() =>
    {
        var owner = new Grid { Width = 280, Height = 100, Resources = LoadTheme() };
        var input = new TextBox
        {
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"Lap {i}"))
        };
        owner.Children.Add(input);
        Layout(owner);
        Assert.IsNotNull(input.Template.FindName("PART_ContentHost", input));
        input.Select(0, 5);
        Assert.AreEqual("Lap 0", input.SelectedText);
        input.SelectedText = "Race";
        Assert.IsTrue(input.Text.StartsWith("Race\n", StringComparison.Ordinal));
        input.ScrollToEnd();
        Layout(owner);
        Assert.IsTrue(input.VerticalOffset > 0, "Multiline editors must retain scrolling.");
        input.IsEnabled = false;
        Assert.IsNotNull(input.Background);
        input.IsEnabled = true;
        input.SelectAll();
        Assert.AreEqual(input.Text, input.SelectedText);
    });

    private static ResourceDictionary LoadTheme()
    {
        using var source = typeof(SharedControlThemeTests).Assembly.GetManifestResourceStream("LazyForzaTheme.xaml")!;
        return (ResourceDictionary)XamlReader.Load(source);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }
}
