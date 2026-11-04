using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace AnimePlayer;

public partial class App : Application
{
    // one per data folder, so a test copy with its own HAKU_DATA_DIR doesn't hand files to the everyday one
    public static readonly string InstanceKey = "HakuPlayer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Settings.DataDir.ToLowerInvariant())))[..16];
    static Mutex? instance;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    void OnGlassLoaded(object s, RoutedEventArgs e) => GlassFrost.Attach((System.Windows.Controls.ContentControl)s);

    protected override void OnStartup(StartupEventArgs e)
    {
        // a video dropped on the exe while the app is open: play it in that window instead of opening a second one
        instance = new Mutex(true, InstanceKey, out bool first);
        if (!first && e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            try
            {
                AllowSetForegroundWindow(-1); // we were just launched by the user, so we may let it come to the front
                using var c = new NamedPipeClientStream(".", InstanceKey, PipeDirection.Out);
                c.Connect(3000);
                using (var w = new StreamWriter(c)) w.WriteLine(Path.GetFullPath(e.Args[0]));
                Environment.Exit(0);
            }
            catch { } // it didn't answer: open normally
        }
        // tooltips: copy the screen behind them for their frost before they show (GlassFrost.cs)
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((s, _) => GlassFrost.Grab((FrameworkElement)s)), true);
        Theme.Install();
#if DEBUG
        // Self-check for the filename -> title cleanup (real filenames from the library).
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\CodeGeassR2-OP1-Encoded.23.976fps.cugan-pro-no-denoise-up2x.fp16.32041c.mp4") == "Code Geass R2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\ShingekiNoKyojinS2-OP1-NCBD1080.webm") == "Shingeki No Kyojin S2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\ShingekiNoKyojinS4Part2-OP1-Encoded.23.976fps.mp4") == "Shingeki No Kyojin S4 Part 2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\[Reaktor] Psycho-Pass S01 - E01 [1080p][x265][10-bit][Dual-Audio]-Encoded.23.976fps.mkv") == "Psycho-Pass S01 - E01");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\S01E01 - The Day a New Demon Was Born-Encoded.23.976fps.mkv") == "S01E01 - The Day a New Demon Was Born");
        Debug.Assert(AnimePlayer.MainWindow.ShowGuess(@"x\[Grp] Naruto - 012 [1080p].mkv") == "Naruto");
        Debug.Assert(AnimePlayer.MainWindow.ShowGuess(@"x\Rick.and.Morty.S01E02.1080p.mkv") == "Rick and Morty");
        Debug.Assert(AnimePlayer.MainWindow.ShowGuess(@"x\KimetsuNoYaiba-OP1.webm", true) == "Kimetsu No Yaiba");
        // Self-check: the series name and year from badly named episode files (for finding a show's details)
        Debug.Assert(InfoService.SeriesFromFile(@"x\[CBM]_Monster_-_01_-_Herr_Dr._Tenma_[5CBB3C92].mkv") is ("Monster", null));
        Debug.Assert(InfoService.SeriesFromFile(@"x\Monster.2004.S01E01.Herr.Dr.Tenma.1080p.BluRay.DUAL.FLAC.2.0.x264-Kitsune.mkv") is ("Monster", 2004));
        Debug.Assert(InfoService.SeriesFromFile(@"x\[Anime Time] Neon Genesis Evangelion - 01.mkv")?.Title == "Neon Genesis Evangelion");
        // Self-check for the upscaler's Auto tier (speeds relative to an RTX 4070)
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 23.976, 1.0, Upscaler.AnimeJaNai)?.Key == "balanced"); // 4070, 1080p film
        Debug.Assert(Upscaler.AutoFor(1280, 720, 23.976, 1.0, Upscaler.AnimeJaNai)?.Key == "quality");   // 720p leaves room for Compact
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 23.976, 0.55, Upscaler.AnimeJaNai)?.Key == "fast");    // RTX 3060
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 60, 1.0, Upscaler.AnimeJaNai)?.Key == "fast");         // 60 fps video
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 23.976, 2.2, Upscaler.AnimeJaNai)?.Key == "quality");  // RTX 4090
        Debug.Assert(Upscaler.AutoFor(3840, 2160, 23.976, 0.3, Upscaler.AnimeJaNai) == null);            // nothing keeps up
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 23.976, 1.0, Upscaler.Adore)?.Key == "adore");    // 4070, 1080p film: Adore
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 29.97, 1.0, Upscaler.Adore)?.Key == "fast");      // 30 fps: too fast for Adore and Balanced
        Debug.Assert(Upscaler.AutoFor(1920, 1080, 23.976, 0.55, Upscaler.Adore)?.Key == "fast");    // else AnimeJaNai's faster tiers
#endif
        base.OnStartup(e);
    }
}
