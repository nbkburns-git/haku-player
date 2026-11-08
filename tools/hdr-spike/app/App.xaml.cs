using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace HdrSpike;

// Settings come from environment variables, so a script can drive every run:
//   SPIKE_FILE   video to play            SPIKE_MODE  sdr | pq | fp16 (swap chain format and colour space)
//   SPIKE_LOG    log file                 SPIKE_DUMP  raw dump of the presented frame, then quit
//   SPIKE_START  seconds to start at      SPIKE_GLASS 1 = Haku's glass shader with one rectangle in the middle
//   SPIKE_VF     mpv video filter         SPIKE_HWDEC mpv hwdec (default auto-safe)
//   SPIKE_RESIZE 1 = resize sweep, then quit
//   SPIKE_QUIT   seconds, then quit       SPIKE_OPTS  extra mpv options "a=b|c=d"
//   HAKU_TEST_WINDOW x,y,w,h in screen pixels (shown there without taking focus)
public partial class App : Application
{
    public static readonly string? LogFile = Environment.GetEnvironmentVariable("SPIKE_LOG");
    static readonly object logLock = new();
    public static void Log(string what)
    {
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var line = $"{ms,8:0} ms  {what}";
        Debug.WriteLine(line);
        if (LogFile == null) return;
        lock (logLock) File.AppendAllText(LogFile, line + "\n");
    }

    [STAThread]
    static void Main()
    {
        Log("main");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log("UNHANDLED: " + e.Exception); };
    }

    SpikeWindow? window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("launched");
        window = new SpikeWindow();
        window.ShowQuietly();
    }
}

sealed class SpikeWindow : Window
{
    readonly SwapChainPanel panel = new();
    readonly TextBlock stats = new() { Foreground = new SolidColorBrush(Colors.White), FontSize = 14, Margin = new Thickness(16), IsHitTestVisible = false };
    readonly Border pill;
    Video? video;
    readonly IntPtr hwnd;

    public SpikeWindow()
    {
        Title = "HDR spike";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var probe = new Button { Content = "Probe", Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetName(probe, "Probe");
        probe.Click += (_, _) => Log("probe button invoked");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "XAML over the video: sharp text?", Foreground = new SolidColorBrush(Colors.White), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(probe);
        pill = new Border
        {
            Child = row, Padding = new Thickness(20, 10, 20, 10), CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x24, 0x30)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40),
        };

        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(panel);
        root.Children.Add(stats);
        root.Children.Add(pill);
        Content = root;

        panel.Loaded += (_, _) =>
        {
            Log("panel loaded");
            try { video = new Video(panel, hwnd, stats); }
            catch (Exception e) { Log("VIDEO FAILED: " + e); stats.Text = e.Message; }
        };
        panel.SizeChanged += (_, e) => video?.Resize();
        panel.CompositionScaleChanged += (_, _) => video?.Resize();
        Closed += (_, _) => { video?.Dispose(); video = null; };
        EventHandler<object>? first = null;
        first = (_, _) => { CompositionTarget.Rendering -= first; Log("first XAML frame"); };
        CompositionTarget.Rendering += first;

        if (Environment.GetEnvironmentVariable("SPIKE_RESIZE") == "1")
            panel.Loaded += async (_, _) => await ResizeSweep();
        if (double.TryParse(Environment.GetEnvironmentVariable("SPIKE_QUIT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q))
            panel.Loaded += async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(q)); Log("quit timer"); Close(); };
    }

    static void Log(string s) => App.Log(s);

    // Shown at HAKU_TEST_WINDOW without activating, so it never takes focus from a game.
    public void ShowQuietly()
    {
        var aw = AppWindow;
        if (Environment.GetEnvironmentVariable("HAKU_TEST_WINDOW")?.Split(',') is { Length: 4 } p && p.All(x => int.TryParse(x, out _)))
            aw.MoveAndResize(new RectInt32(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])));
        var before = GetForegroundWindow();
        aw.Show(false);
        Log($"window shown (activate=false); foreground kept: {GetForegroundWindow() == before}, ours is foreground: {GetForegroundWindow() == hwnd}");
    }

    // Resizes the window through a range of sizes and logs how long each takes to show the video at the new size.
    async Task ResizeSweep()
    {
        await Task.Delay(2500);
        var aw = AppWindow;
        var start = aw.Size;
        int[] widths = { 1200, 1600, 900, 2000, 1400, 1100, 1800, start.Width };
        foreach (var w in widths)
        {
            int h = (int)(w * 9 / 16.0) + 40;
            video?.MarkResize(w);
            aw.Resize(new SizeInt32(w, h));
            await Task.Delay(700);
        }
        // a quick drag-like sweep: 40 steps 16 ms apart
        for (int i = 0; i <= 40; i++)
        {
            int w = 1200 + (int)(800 * Math.Sin(i / 40.0 * Math.PI));
            video?.MarkResize(w);
            aw.Resize(new SizeInt32(w, (int)(w * 9 / 16.0) + 40));
            await Task.Delay(16);
        }
        await Task.Delay(800);
        video?.ReportResizes();
        Close();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
