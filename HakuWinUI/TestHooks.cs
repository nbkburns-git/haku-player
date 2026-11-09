using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AnimePlayer;

// Test runs only (HAKU_TEST_WINDOW set): the tests press buttons through this pipe instead of UI Automation, because
// pressing a WinUI button through UI Automation brings its window to the front (over whatever you're doing). Pressed in
// here, by the button's own automation peer, nothing is activated. Commands, one per line, answered "ok" or an error:
//   press <name>            a button (or anything invokable) by its automation name
//   text <name>|<text>      a text box's text
//   names                   every automation name on screen (for writing tests)
public partial class MainWindow
{
    void StartTestHooks()
    {
        if (TestFocus.Rect == null) return;
        _ = ListenForTests($"haku-test-{Environment.ProcessId}");
    }

    async Task ListenForTests(string pipe)
    {
        while (true)
        {
            try
            {
                using var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await s.WaitForConnectionAsync();
                var r = new StreamReader(s); var w = new StreamWriter(s) { AutoFlush = true };
                string? line;
                while ((line = await r.ReadLineAsync()) != null) await w.WriteLineAsync(TestCommand(line));
            }
            catch { await Task.Delay(200); }
        }
    }

    string TestCommand(string line)
    {
        int sp = line.IndexOf(' ');
        string cmd = sp < 0 ? line : line[..sp], arg = sp < 0 ? "" : line[(sp + 1)..];
        switch (cmd)
        {
            case "press":
                if (Named(arg) is not UIElement el) return $"not found: {arg}";
                if (FrameworkElementAutomationPeer.CreatePeerForElement(el) is { } peer && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv) { inv.Invoke(); return "ok"; }
                return $"can't press: {arg}";
            case "text":
                var parts = arg.Split('|', 2);
                if (Named(parts[0]) is not TextBox box) return $"not a text box: {parts[0]}";
                box.Text = parts.Length > 1 ? parts[1] : "";
                return "ok";
            case "wheel": // a wheel notch to a scroller (as the mouse would), its offset logged every frame for a second
            {
                var wa = arg.Split(' '); // "<delta>" (the library's cards) or "<delta> episodes" (a show's list)
                var sv = wa.Length > 1 && wa[1] == "episodes" ? episodeScroll : cardScroll; double d = double.Parse(wa[0], System.Globalization.CultureInfo.InvariantCulture);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                void Log(object? s, object e) { MpvCore.Note($"scroll offset {sw.ElapsedMilliseconds} {sv.VerticalOffset:0.0}"); if (sw.ElapsedMilliseconds > 1200) CompositionTarget.Rendering -= Log; }
                CompositionTarget.Rendering += Log;
                SmoothScroll.Wheels[sv](d);
                return "ok";
            }
            case "names":
                return string.Join(" | ", All(root).Select(AutomationProperties.GetName).Where(n => n.Length > 0).Distinct());
        }
        return $"unknown command: {cmd}";
    }

    // the first visible element with this automation name
    UIElement? Named(string name) => All(root).FirstOrDefault(e => AutomationProperties.GetName(e) == name && IsShown(e));

    static System.Collections.Generic.IEnumerable<UIElement> All(DependencyObject d)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is UIElement { Visibility: Visibility.Collapsed }) continue;
            if (c is UIElement u) yield return u;
            foreach (var x in All(c)) yield return x;
        }
    }

    static bool IsShown(UIElement e)
    {
        for (DependencyObject? d = e; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
}
