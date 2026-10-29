using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace AnimePlayer;

// Fades without a ghost. The live blur is drawn by mpv, which shows a change only ~0.1 s later (it renders each frame
// ahead of time, queues it, and redraws only at the video's frame rate; more when the PC is busy), while the rest of a
// bar is drawn by WPF straight away. No timing keeps two renderers with a varying delay in step, so during a fade WPF
// draws the whole bar itself, with its blurred snapshot of the current frame, and mpv's blur is off:
//   fading out: mpv's blur goes off at the start (hidden under WPF's own frost until it does), WPF fades everything;
//   fading in:  live blur straight away (it may arrive a moment after the bar, but never shows an old picture).
public partial class PlayerControls
{
    readonly HashSet<FrameworkElement> handoff = new();  // elements whose live blur is off while WPF draws them
    readonly Dictionary<FrameworkElement, int> fadeGen = new();

    // a snapshot of (about) the current frame to draw the frost from
    bool FrostReady => lastFrame != null && Math.Abs(frostSecond - time) <= 3;

    // mpv's blur strength for an element (0 while WPF draws it alone)
    double LiveAlpha(FrameworkElement el) => handoff.Contains(el) ? 0 : el.Opacity;

    void GlassFade(FrameworkElement el, GlassSurface glass, double? from, double to, TimeSpan d, IEasingFunction? ease, Action? done = null)
    {
        int gen = fadeGen[el] = fadeGen.GetValueOrDefault(el) + 1;
        // only when fading out: that's where mpv's late blur outlived the bar. Fading in from the snapshot showed an old
        // picture for half a second, so a bar coming back uses the live blur straight away.
        bool hand = LiveGlass && FrostReady && to <= 0;
        if (hand) { handoff.Add(el); glass.Live = false; }
        else if (handoff.Remove(el)) glass.Live = true; // came back mid fade-out: straight back to the live blur
        var a = from is double f ? new DoubleAnimation(f, to, d) : new DoubleAnimation(to, d);
        a.EasingFunction = ease;
        a.Completed += async (_, _) =>
        {
            if (fadeGen.GetValueOrDefault(el) != gen) return; // a newer fade took over
            done?.Invoke();
            handoff.Remove(el); // faded out: alpha 0 anyway; faded in: mpv's blur comes on under the frost
            PushGlass(null, EventArgs.Empty);
            SettleGlass();
            if (!hand) return;
            if (to <= 0) { glass.Live = true; return; }
            await Task.Delay(250); // ~2x mpv's delay: its blur is on screen before the frost goes
            if (fadeGen.GetValueOrDefault(el) == gen) glass.Live = true;
        };
        WatchGlass();
        el.BeginAnimation(UIElement.OpacityProperty, a);
        PushGlass(null, EventArgs.Empty); // turn mpv's blur off now, not a frame later
    }
}
