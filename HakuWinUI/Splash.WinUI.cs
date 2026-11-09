using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AnimePlayer;

// The loading screen, as the WPF app's: a spinning glass ring round the breathing logo, the wordmark, "Loading your
// library…"; it fades out (growing a touch) once the library is in.
public partial class MainWindow
{
    readonly Grid splash = new() { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0B, 0x10)) };
    Storyboard? splashSpin;

    void SetupSplash()
    {
        var ringTurn = new RotateTransform { CenterX = 95, CenterY = 95 };
        var ring = new Grid { Width = 200, Height = 200 };
        ring.Children.Add(new Ellipse { Width = 190, Height = 190, StrokeThickness = 3, Stroke = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) });
        ring.Children.Add(new Ellipse { Width = 190, Height = 190, StrokeThickness = 3, StrokeDashArray = { 18, 60 }, StrokeDashCap = PenLineCap.Round, Stroke = ThemeW.Grad, RenderTransform = ringTurn });
        var pulse = new ScaleTransform { CenterX = 60, CenterY = 60 };
        var logo = ThemeW.Logo(120);
        logo.RenderTransform = pulse;
        ring.Children.Add(logo);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(ring);
        var mark = new TextBlock { FontFamily = Outfit, FontSize = 38, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Foreground = TextBrush };
        mark.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "haku", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        mark.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "player", FontWeight = Microsoft.UI.Text.FontWeights.Light, Foreground = ThemeW.Light });
        AutomationProperties.SetName(mark, "Haku Player");
        col.Children.Add(mark);
        col.Children.Add(new TextBlock { Text = "Loading your library…", FontSize = 15, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        splash.Children.Add(col);
        root.Children.Add(splash);

        splashSpin = new Storyboard();
        void Anim(DependencyObject target, string prop, double from, double to, double secs, bool back, EasingFunctionBase? ease = null)
        {
            var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromSeconds(secs), RepeatBehavior = RepeatBehavior.Forever, AutoReverse = back, EasingFunction = ease };
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop); splashSpin.Children.Add(a);
        }
        Anim(ringTurn, "Angle", 0, 360, 1.4, false);
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        Anim(pulse, "ScaleX", 0.94, 1.04, 0.9, true, sine);
        Anim(pulse, "ScaleY", 0.94, 1.04, 0.9, true, sine);
        splashSpin.Begin();
    }

    void HideSplash()
    {
        if (splash.Visibility != Visibility.Visible) return;
        splash.IsHitTestVisible = false;
        splash.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(450) };
        splash.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(450) };
        splash.CenterPoint = new Vector3((float)splash.ActualWidth / 2, (float)splash.ActualHeight / 2, 0);
        splash.Opacity = 0;
        splash.Scale = new Vector3(1.08f, 1.08f, 1);
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(470);
        t.Tick += (_, _) => { t.Stop(); splash.Visibility = Visibility.Collapsed; splashSpin?.Stop(); }; // its spin and pulse repeat forever
        t.Start();
    }
}
