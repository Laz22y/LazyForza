using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class DisabledListThemeTests
{
    [TestMethod]
    public void DisabledOwnerPreservesDarkListSurfaceSelectionAndScrollingAfterReenable()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Read the production theme without constructing the application's startup lifecycle.
                using var source = typeof(DisabledListThemeTests).Assembly.GetManifestResourceStream("LazyForzaTheme.xaml")!;
                var document = XDocument.Load(source);
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = document.Root!.Element(presentation + "Application.Resources")!;
                var dictionary = new XElement(presentation + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    resources.Elements().Where(element => element.Name.LocalName == "SolidColorBrush" ||
                        element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") is "ListBox" or "ListBoxItem")
                        .Select(element => new XElement(element)));
                var theme = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                var owner = new Grid { Width = 280, Height = 320, Resources = theme, Background = (Brush)theme["PanelBrush"] };
                var list = new ListBox();
                list.Items.Add("Overview"); list.Items.Add("Race"); list.SelectedIndex = 0;
                owner.Children.Add(list);
                void Layout()
                {
                    owner.Measure(new Size(280, 320)); owner.Arrange(new Rect(0, 0, 280, 320)); owner.UpdateLayout();
                }
                Color SampleSurface()
                {
                    Layout();
                    var bitmap = new RenderTargetBitmap(280, 320, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(owner);
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(180, 290, 1, 1), pixel, 4, 0);
                    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                }
                var surface = ((SolidColorBrush)theme["PanelBrush"]).Color;
                Assert.AreEqual(surface, SampleSurface());
                owner.IsEnabled = false;
                Assert.IsFalse(list.IsEnabled, "Mandatory update must continue blocking owner interaction.");
                Assert.AreEqual(surface, SampleSurface(), "Disabled lists must not replace the dark surface with system white.");
                Assert.AreEqual(0, list.SelectedIndex);
                owner.IsEnabled = true;
                Assert.AreEqual(surface, SampleSurface());
                for (var i = 0; i < 80; i++) list.Items.Add($"Track {i}");
                Layout();
                var scroll = Descendants<ScrollViewer>(list).First();
                Assert.IsTrue(scroll.ScrollableHeight > 0);
                scroll.ScrollToBottom(); Layout();
                Assert.IsTrue(scroll.VerticalOffset > 0, "List scrolling must recover when the update finishes.");
                list.SelectedIndex = list.Items.Count - 1;
                Assert.AreEqual(81, list.SelectedIndex);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
