using System.Windows;

namespace LazyForza.App;

/// <summary>Apply the shared Window style explicitly to derived secondary windows.</summary>
internal class AppWindow : Window
{
    protected AppWindow()
    {
        SetResourceReference(StyleProperty, typeof(Window));
        WindowTheme.UseDarkTitleBar(this);
        ShowInTaskbar = false;
        Loaded += (_, _) => Title = AppLocalization.Literal(Title);
    }
}
