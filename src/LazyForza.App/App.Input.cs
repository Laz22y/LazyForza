using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LazyForza.App;

public partial class App
{
    private readonly HashSet<ComboBox> keyboardScrolling = [];

    private void ComboBox_PreviewKeyboardInput(object sender, RoutedEventArgs e)
    {
        var comboBox = (ComboBox)sender;
        if (!keyboardScrolling.Add(comboBox)) return;
        // Keep focus/selection requests caused by this key or text-search input.
        // Mouse movement alone does not update InputManager.MostRecentInputDevice.
        _ = comboBox.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => keyboardScrolling.Remove(comboBox)));
    }

    private void ComboBoxItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // WPF focuses items on mouse enter. Bringing a partially visible item into
        // view exposes the next row under the pointer, causing repeated scrolling.
        // Keep keyboard navigation and explicit scrolling through the normal path.
        if (sender is ComboBoxItem { IsMouseOver: true } item &&
            ReferenceEquals(e.TargetObject, item) &&
            ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true } comboBox &&
            !keyboardScrolling.Contains(comboBox) &&
            Mouse.LeftButton == MouseButtonState.Released)
        {
            e.Handled = true;
        }
    }
}
