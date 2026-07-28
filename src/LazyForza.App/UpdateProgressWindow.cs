using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock status;
    private readonly ProgressBar progressBar;
    private readonly Button cancel;
    private readonly CancellationTokenSource cancellation = new();
    private bool completed;

    public UpdateProgressWindow(Window owner, string version)
    {
        Owner = owner;
        Title = "更新 LazyForza";
        Width = 460;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("PanelBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = $"正在准备 LazyForza {version}",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });
        status = new TextBlock
        {
            Text = "正在连接 GitCode，必要时使用 GitHub…",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 7, 0, 10)
        };
        stack.Children.Add(status);
        progressBar = new ProgressBar
        {
            Height = 7,
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 1
        };
        stack.Children.Add(progressBar);
        cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(0, 14, 0, 0)
        };
        cancel.Click += (_, _) => cancellation.Cancel();
        stack.Children.Add(cancel);
        Content = stack;
        Closing += (_, args) =>
        {
            if (completed) return;
            cancellation.Cancel();
            args.Cancel = true;
            status.Text = "正在取消…";
            cancel.IsEnabled = false;
        };
    }

    public CancellationToken CancellationToken => cancellation.Token;

    public IProgress<UpdateProgress> Progress => new Progress<UpdateProgress>(value =>
    {
        status.Text = value.Stage;
        if (value.Fraction is { } fraction)
        {
            progressBar.IsIndeterminate = false;
            progressBar.Value = fraction;
        }
        else
        {
            progressBar.IsIndeterminate = true;
        }
    });

    public void Finish()
    {
        if (completed) return;
        completed = true;
        Close();
        cancellation.Dispose();
    }
}
