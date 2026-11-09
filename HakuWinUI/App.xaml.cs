using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace AnimePlayer;

// Haku Player on WinUI 3 (docs/winui3-migration-plan.md, phase 2: the player). Run with a video to play it, or drop one on
// the window. The library pages come in phase 3; until then the WPF app is the everyday one.
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => MpvCore.Note("UNHANDLED: " + e.Exception);
    }

    static System.Threading.Mutex? instance;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MpvCore.Note("launched");
        TestFocus.Install(); // test runs: never the foreground window (even when UI Automation presses a button)
        // unpackaged: the command line is the process's own (args.Arguments is empty). Videos and folders of them; a video
        // first and a folder after it plays the folder starting at that video (tests open a season that way)
        var paths = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--") && (System.IO.File.Exists(a) || System.IO.Directory.Exists(a))).ToList();
        // already open: hand the video to that window and leave (a test copy with its own data folder has its own key)
        instance = new System.Threading.Mutex(true, MainWindow.InstanceKey, out bool first);
        if (!first && paths.Count == 1 && System.IO.File.Exists(paths[0]))
        {
            try
            {
                AllowSetForegroundWindow(-1); // launched by you: the open window may come to the front
                using var c = new System.IO.Pipes.NamedPipeClientStream(".", MainWindow.InstanceKey, System.IO.Pipes.PipeDirection.Out);
                c.Connect(3000);
                using (var w = new System.IO.StreamWriter(c)) w.WriteLine(System.IO.Path.GetFullPath(paths[0]));
                Exit();
                return;
            }
            catch { } // it didn't answer: open normally
        }
        window = new MainWindow();
        window.ShowQuietly();
        if (paths.Count == 2 && System.IO.File.Exists(paths[0]) && System.IO.Directory.Exists(paths[1]))
        {
            var list = MainWindow.VideosOf(new[] { paths[1] });
            int at = list.FindIndex(f => string.Equals(f, System.IO.Path.GetFullPath(paths[0]), StringComparison.OrdinalIgnoreCase));
            window.Play(list, Math.Max(0, at));
        }
        else if (paths.Count > 0 && !(paths.Count == 1 && System.IO.Directory.Exists(paths[0]))) window.Play(MainWindow.VideosOf(paths), 0);
        // (a single folder is a library root, as in the WPF app: MainWindow.ArgRoot)
    }
}
