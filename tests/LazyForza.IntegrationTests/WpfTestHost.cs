using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

// WPF allows one Application per test process, even after Shutdown. Keep its
// dispatcher alive so resources never belong to a terminated test thread.
internal static class WpfTestHost
{
    private static readonly Lazy<Task<Application>> Host = new(Start);

    public static void Run(Action action)
    {
        var application = Host.Value.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        application.Dispatcher.InvokeAsync(() =>
        {
            var language = AppLocalization.CurrentLanguage;
            var existingWindows = application.Windows.Cast<Window>().ToHashSet();
            try { action(); }
            finally
            {
                foreach (var window in application.Windows.Cast<Window>().Where(window => !existingWindows.Contains(window)).ToArray())
                    window.Close();
                AppLocalization.UseLanguage(language);
            }
        }).Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public static void Shutdown()
    {
        if (!Host.IsValueCreated || !Host.Value.IsCompletedSuccessfully) return;
        var application = Host.Value.Result;
        application.Dispatcher.Invoke(application.Shutdown);
    }

    private static Task<Application> Start()
    {
        var ready = new TaskCompletionSource<Application>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/LazyForza.App;component/Themes/Controls.xaml", UriKind.Relative)
                });
                // App.xaml owns the dropdown templates; Controls.xaml alone falls
                // back to the Windows theme. Keep previews on the shipping templates.
                using var appXaml = typeof(WpfTestHost).Assembly.GetManifestResourceStream("LazyForzaApp.xaml")!;
                var xml = XDocument.Load(appXaml);
                foreach (var type in new[] { typeof(ComboBox), typeof(ComboBoxItem) })
                {
                    var style = xml.Descendants().Single(element => element.Name.LocalName == "Style" &&
                        (string?)element.Attribute("TargetType") == type.Name);
                    // Event handlers require the real App instance and are not visual resources.
                    style.Elements().Where(element => element.Name.LocalName == "EventSetter").Remove();
                    application.Resources[type] = (Style)System.Windows.Markup.XamlReader.Parse(style.ToString());
                }
                ready.SetResult(application);
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "WPF test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private sealed class TestApplication : Application
    {
        // Load real styles without starting telemetry, initialization or updates.
        protected override void OnStartup(StartupEventArgs e) { }
    }
}

[TestClass]
public sealed class WpfTestLifetime
{
    [AssemblyCleanup]
    public static void Cleanup() => WpfTestHost.Shutdown();
}
