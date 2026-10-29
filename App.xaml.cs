using System.Diagnostics;
using System.Windows;

namespace AnimePlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Theme.Install();
#if DEBUG
        // Self-check for the filename -> title cleanup (real filenames from the library).
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\CodeGeassR2-OP1-Encoded.23.976fps.cugan-pro-no-denoise-up2x.fp16.32041c.mp4") == "Code Geass R2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\ShingekiNoKyojinS2-OP1-NCBD1080.webm") == "Shingeki No Kyojin S2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\ShingekiNoKyojinS4Part2-OP1-Encoded.23.976fps.mp4") == "Shingeki No Kyojin S4 Part 2 – OP1");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\[Reaktor] Psycho-Pass S01 - E01 [1080p][x265][10-bit][Dual-Audio]-Encoded.23.976fps.mkv") == "Psycho-Pass S01 - E01");
        Debug.Assert(AnimePlayer.MainWindow.Friendly(@"x\S01E01 - The Day a New Demon Was Born-Encoded.23.976fps.mkv") == "S01E01 - The Day a New Demon Was Born");
#endif
        base.OnStartup(e);
    }
}
