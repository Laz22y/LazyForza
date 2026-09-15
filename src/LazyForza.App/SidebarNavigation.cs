using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LazyForza.App;

/// <summary>Two selectors share stable page IDs; only the upper navigation scrolls.</summary>
internal sealed class SidebarNavigation : Grid
{
    private readonly ListBox pages = new(), settings = new();
    private readonly StackPanel footer = new();
    private int selectedIndex = -1;
    private bool selecting;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? CompactChanged;
    internal bool IsCompact { get; private set; }
    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value == selectedIndex) return;
            var target = pages.Items.Cast<ListBoxItem>().Concat(settings.Items.Cast<ListBoxItem>())
                .FirstOrDefault(item => (int)item.Tag == value);
            if (value >= 0 && target is null) return;
            selecting = true;
            pages.SelectedItem = pages.Items.Contains(target) ? target : null;
            settings.SelectedItem = settings.Items.Contains(target) ? target : null;
            selecting = false;
            selectedIndex = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal SidebarNavigation()
    {
        Margin = new Thickness(12, 12, 12, 12);
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var list in new[] { pages, settings })
        {
            list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "SidebarNavigationItem");
            list.Background = System.Windows.Media.Brushes.Transparent;
            list.BorderThickness = new Thickness(0);
            list.Padding = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, list == pages ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            list.SelectionChanged += (_, _) =>
            {
                if (!selecting && list.SelectedItem is ListBoxItem item) SelectedIndex = (int)item.Tag;
            };
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Down && list == pages && pages.SelectedIndex == pages.Items.Count - 1)
                    FocusPage(settings.Items.Cast<ListBoxItem>().First(), e);
                else if (e.Key == Key.Up && list == settings && pages.Items.Count > 0)
                    FocusPage(pages.Items.Cast<ListBoxItem>().Last(), e);
            };
        }
        Children.Add(pages);
        SetRow(footer, 1); Children.Add(footer);
        SizeChanged += (_, _) =>
        {
            var compact = ActualHeight < 700;
            if (compact == IsCompact) return;
            IsCompact = compact;
            CompactChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void FocusPage(ListBoxItem item, KeyEventArgs args)
    {
        SelectedIndex = (int)item.Tag;
        item.Focus(); args.Handled = true;
    }

    internal void SetFooter(UIElement status, UIElement quickSettings)
    {
        footer.Children.Clear();
        var divider = new Border { Height = 1, Margin = new Thickness(10, 12, 10, 10) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        footer.Children.Add(divider);
        footer.Children.Add(status);
        footer.Children.Add(quickSettings);
        footer.Children.Add(settings);
    }

    internal void ClearPages()
    {
        selecting = true;
        pages.Items.Clear(); settings.Items.Clear(); selectedIndex = -1;
        selecting = false;
    }

    internal void AddPage(int id, UIElement content, bool bottom = false)
    {
        var item = new ListBoxItem { Content = content, Tag = id, Height = 44,
            Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(14, 8, 14, 8) };
        (bottom ? settings : pages).Items.Add(item);
    }
}
