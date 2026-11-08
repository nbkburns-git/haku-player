using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

static class Program
{
    static void Log(string s)
    {
        var line = $"{(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds,8:0} ms  {s}\n";
        if (Environment.GetEnvironmentVariable("SPIKE_LOG") is { } f) File.AppendAllText(f, line);
    }

    [STAThread]
    static void Main()
    {
        Log("main");
        var app = new Application();
        var w = new Window { Title = "WPF baseline", Background = Brushes.Black, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        if (Environment.GetEnvironmentVariable("HAKU_TEST_WINDOW")?.Split(',') is { Length: 4 } p)
        { w.Left = int.Parse(p[0]); w.Top = int.Parse(p[1]); w.Width = int.Parse(p[2]); w.Height = int.Parse(p[3]); }
        var b = new Button { Content = "Probe", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40) };
        w.Content = new Grid { Children = { new TextBlock { Text = "WPF baseline", Foreground = Brushes.White, Margin = new Thickness(16) }, b } };
        w.ContentRendered += (_, _) => { Log("first frame"); w.Dispatcher.BeginInvoke(() => app.Shutdown(), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };
        app.Run(w);
    }
}
