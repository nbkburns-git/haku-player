using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace AnimePlayer;

// A looping drift (the home page's cover walls and colour glows) that really stops when nobody can see it.
// A merely paused WPF clock still keeps WPF drawing every frame: measured 60 render passes a second during playback,
// 0 once stopped, and that steady drawing also held mouse and keyboard input back. Hold leaves the element exactly
// where it is; Go carries on from the same point.
sealed class Drift
{
    readonly Animatable target;
    readonly DependencyProperty prop;
    readonly AnimationClock clock;
    TimeSpan? heldAt;

    public Drift(Animatable target, DependencyProperty prop, AnimationTimeline anim)
    {
        this.target = target; this.prop = prop;
        clock = anim.CreateClock();
        target.ApplyAnimationClock(prop, clock);
    }

    public void Hold()
    {
        if (heldAt != null || clock.Controller == null) return;
        heldAt = clock.CurrentTime ?? TimeSpan.Zero;
        var now = target.GetValue(prop);
        clock.Controller.Stop();
        target.SetValue(prop, now); // stopped, the property falls back to its base value: make that where it was
    }

    public void Go()
    {
        if (heldAt is not TimeSpan at || clock.Controller == null) return;
        heldAt = null;
        clock.Controller.Begin();
        clock.Controller.Seek(at, TimeSeekOrigin.BeginTime);
    }

    public void End() => clock.Controller?.Stop();
}
