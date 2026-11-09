using System.Collections.Generic;
using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AnimePlayer;

// The WPF app's App.xaml styles, for WinUI: chrome-less buttons (Flat), the glass pill (Glass / Back), window caption
// buttons, the player's icon buttons, filter chips, list rows, and the shared glass brushes. Same sizes, colours and
// hover behaviour, so the two apps look the same.
static partial class GlassLook
{
    public static readonly SolidColorBrush TextB = new(Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF7));
    public static readonly SolidColorBrush MutedB = new(Color.FromArgb(0xFF, 0xA3, 0xA8, 0xB5));
    public static LinearGradientBrush GlassFill() => Grad(new Point(0.4, 1), (0x1F, 0), (0x0C, 1));
    public static LinearGradientBrush GlassFillHover() => Grad(new Point(0.4, 1), (0x33, 0), (0x14, 1));
    public static LinearGradientBrush GlassStroke() => Grad(new Point(0, 1), (0x66, 0), (0x14, 0.45), (0x0A, 1));

    // white at these alphas along a gradient from the top-left
    static LinearGradientBrush Grad(Point end, params (byte A, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end };
        foreach (var (a, at) in stops) b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(a, 0xFF, 0xFF, 0xFF), Offset = at });
        return b;
    }

    // a Button with no chrome at all: just its content (WPF's Flat template)
    static ControlTemplate? plain;
    public static ControlTemplate Plain => plain ??= (ControlTemplate)XamlReader.Load(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<ContentPresenter Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' Content='{TemplateBinding Content}'" +
        " HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}'" +
        " CornerRadius='{TemplateBinding CornerRadius}' Foreground='{TemplateBinding Foreground}'/></ControlTemplate>");

    public static HandButton Bare(object content, string name, Action? click)
    {
        var b = new HandButton { Template = Plain, Content = content, Background = new SolidColorBrush(Colors.Transparent), Foreground = TextB,
            IsTabStop = false, AllowFocusOnInteraction = false, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(b, name);
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    // hover: grow to this scale (from the middle); press: dim
    public static void HoverScale(FrameworkElement el, float scale, int ms = 180)
    {
        el.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(ms) };
        el.SizeChanged += (_, _) => el.CenterPoint = new Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
        el.PointerEntered += (_, _) => el.Scale = new Vector3(scale, scale, 1);
        el.PointerExited += (_, _) => el.Scale = Vector3.One;
        el.PointerCanceled += (_, _) => el.Scale = Vector3.One;
    }
    static void PressDim(UIElement el, double to)
    {
        el.PointerPressed += (_, _) => el.Opacity = to;
        el.PointerReleased += (_, _) => el.Opacity = 1;
        el.PointerExited += (_, _) => el.Opacity = 1;
        el.PointerCaptureLost += (_, _) => el.Opacity = 1;
    }

    // Glass pill (WPF "Glass"): glass fill and rim, brighter and a touch bigger on hover
    public static HandButton Glass(object content, string name, Action click, Thickness? padding = null, double fontSize = 16)
    {
        var face = new Border { CornerRadius = new CornerRadius(14), Background = GlassFill(), BorderBrush = GlassStroke(), BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(18, 8, 18, 8), Child = content as UIElement ?? new TextBlock { Text = content?.ToString(), FontSize = fontSize, Foreground = TextB } };
        var b = Bare(face, name, click);
        b.PointerEntered += (_, _) => face.Background = GlassFillHover();
        b.PointerExited += (_, _) => face.Background = GlassFill();
        HoverScale(b, 1.015f);
        PressDim(b, 0.75);
        return b;
    }

    // "‹ Back" (WPF "Back")
    public static HandButton Back(Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var arrow = Icon("", 13); arrow.Margin = new Thickness(0, 1, 8, 0); arrow.Foreground = TextB;
        row.Children.Add(arrow);
        row.Children.Add(new TextBlock { Text = "Back", FontSize = 15, Foreground = TextB, VerticalAlignment = VerticalAlignment.Center });
        var b = Glass(row, "Back", click, new Thickness(12, 5, 16, 5));
        ToolTipService.SetToolTip(b, "Back (Esc / Backspace)");
        return b;
    }

    // Window caption button (WPF "Caption"): quiet, brightens and grows on hover; Close turns red
    public static HandButton Caption(string glyph, string name, string tip, Action click, double size = 11, bool close = false, Brush? color = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = color ?? MutedB };
        var b = Bare(icon, name, click);
        b.Width = 40; b.Height = 30;
        b.Background = new SolidColorBrush(Colors.Transparent);
        var rest = icon.Foreground;
        b.PointerEntered += (_, _) => icon.Foreground = close ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)) : TextB;
        b.PointerExited += (_, _) => icon.Foreground = rest;
        HoverScale(icon, 1.15f, 150);
        PressDim(b, 0.7);
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    // Player icon button (WPF "Icon"): no disc; on hover the icon brightens, grows a touch and an accent dot shows
    public static HandButton IconButton(object content, string name, string tip, Action click, double size = 44)
    {
        var dot = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = ThemeW.Grad, VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2), Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) } };
        var c = new ContentControl { Content = content, Opacity = 0.82, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } };
        var g = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        g.Children.Add(c); g.Children.Add(dot);
        var b = Bare(g, name, click);
        b.Width = size; b.Height = size;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Stretch;
        b.PointerEntered += (_, _) => { c.Opacity = 1; dot.Opacity = 1; };
        b.PointerExited += (_, _) => { c.Opacity = 0.82; dot.Opacity = 0; };
        HoverScale(g, 1.14f);
        PressDim(b, 0.7);
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    // Filter / settings chip (WPF "Chip"): accent gradient when on
    public static HandButton Chip(string text, bool on, Action click, double fontSize = 13, string? name = null)
    {
        var label = new TextBlock { Text = text, FontSize = fontSize, Foreground = TextB, HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
        var face = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(1), Child = label,
            Background = on ? ThemeW.Grad : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(on ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) };
        var b = Bare(face, name ?? text, click);
        if (!on)
        {
            b.PointerEntered += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            b.PointerExited += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        }
        return b;
    }

    // List row (WPF "Row"): faint panel; hover brightens it, lights an accent bar on the left and nudges the content right
    public static HandButton Row(UIElement content, string name, Action click, Thickness padding)
    {
        var hi = new Border { CornerRadius = new CornerRadius(14), Background = GlassFillHover(), Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } };
        var accent = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(6, 12, 6, 12), HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeW.Grad, Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } };
        var holder = new ContentControl { Content = content, Margin = padding, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false, TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) } };
        var g = new Grid(); g.Children.Add(hi); g.Children.Add(accent); g.Children.Add(holder);
        var face = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Child = g };
        var b = Bare(face, name, click);
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.PointerEntered += (_, _) => { hi.Opacity = 1; accent.Opacity = 1; holder.Translation = new Vector3(6, 0, 0); face.BorderBrush = GlassStroke(); };
        b.PointerExited += (_, _) => { hi.Opacity = 0; accent.Opacity = 0; holder.Translation = Vector3.Zero; face.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)); };
        PressDim(face, 0.75);
        return b;
    }

    // A soft glow (or shadow) behind text, like WPF's DropShadowEffect on a TextBlock. Composition draws it in a
    // sibling element under the text, so the text has to sit in a Grid cell of its own: returns that Grid.
    public static Grid WithGlow(TextBlock text, Color color, float blur, float opacity)
    {
        var host = new Grid { HorizontalAlignment = text.HorizontalAlignment, VerticalAlignment = text.VerticalAlignment, Margin = text.Margin };
        text.Margin = new Thickness(0);
        var under = new Grid { IsHitTestVisible = false };
        host.Children.Add(under); host.Children.Add(text);
        void Make()
        {
            var comp = ElementCompositionPreview.GetElementVisual(under).Compositor;
            var shadow = comp.CreateDropShadow();
            shadow.Mask = text.GetAlphaMask();
            shadow.Color = color; shadow.BlurRadius = blur; shadow.Opacity = opacity; shadow.Offset = Vector3.Zero;
            var sprite = comp.CreateSpriteVisual();
            sprite.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(under, sprite);
        }
        text.SizeChanged += (_, _) => Make();
        return host;
    }
}

public sealed class CursorGrid : Grid
{
    public void SetCursor(InputCursor? c) => ProtectedCursor = c;
}

// A fully transparent pointer, for hiding it over the video. WinUI shows an element's ProtectedCursor, and turns a
// Win32 cursor into one with IInputCursorStaticsInterop.CreateFromHCursor (microsoft.ui.input.interop.h); the cursor
// itself is a 32x32 one whose masks leave the screen as it is (the way FlyPhotos' MouseAutoHider hides it).
static class HiddenCursor
{
    static InputCursor? made;
    static bool tried;

    public static InputCursor? Get()
    {
        if (tried) return made;
        tried = true;
        try
        {
            var and = new byte[32 * 32 / 8]; Array.Fill(and, (byte)0xFF); // AND 1, XOR 0: the pixel underneath, unchanged
            var xor = new byte[32 * 32 / 8];
            var h = CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, and, xor);
            if (h == IntPtr.Zero) { MpvCore.Note("hidden cursor: CreateCursor failed"); return null; }
            made = FromHCursor(h);
            MpvCore.Note(made != null ? "hidden cursor: transparent cursor ready" : "hidden cursor: CreateFromHCursor unavailable");
        }
        catch (Exception e) { MpvCore.Note($"hidden cursor: {e.Message}"); }
        return made;
    }

    static unsafe InputCursor? FromHCursor(IntPtr hcursor)
    {
        var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
        var iid = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"); // IInputCursorStaticsInterop
        if (System.Runtime.InteropServices.Marshal.QueryInterface(factory.ThisPtr, in iid, out var interop) < 0) return null;
        try
        {
            // IUnknown (3) + IInspectable (3), then CreateFromHCursor
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[6];
            IntPtr cursor;
            if (fn(interop, hcursor, &cursor) < 0 || cursor == IntPtr.Zero) return null;
            try { return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursor); }
            finally { System.Runtime.InteropServices.Marshal.Release(cursor); }
        }
        finally { System.Runtime.InteropServices.Marshal.Release(interop); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] and, byte[] xor);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}

public sealed class HandButton : Button
{
    public HandButton() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}

// WPF's WrapPanel: children left to right, onto the next line when full
public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += line; x = 0; line = 0; }
            x += s.Width; line = Math.Max(line, s.Height); width = Math.Max(width, x);
        }
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += line; x = 0; line = 0; }
            c.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width; line = Math.Max(line, s.Height);
        }
        return final;
    }
}

static partial class ThemeW
{
    // The iceberg logo, as the WPF app's Theme.Logo draws it (same shapes), turned to the accent's hue.
    public static Viewbox Logo(double size, bool small = false)
    {
        var accent = Accent.Color;
        var (hue, sat, _) = Hsl(accent);
        Color Tint(string hex) { var (_, s, l) = Hsl(Parse(hex)!.Value); return FromHsl(hue, Math.Min(1, s * sat), l); }
        Brush Down(string top, string bottom) => new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop { Color = Tint(top), Offset = 0 }, new GradientStop { Color = Tint(bottom), Offset = 1 } },
        };
        var canvas = new Canvas { Width = 256, Height = 256 };
        var tile = new Grid { Width = 240, Height = 240, CornerRadius = new CornerRadius(62) };
        Canvas.SetLeft(tile, 8); Canvas.SetTop(tile, 8);
        canvas.Children.Add(tile);
        var art = new Canvas { Width = 240, Height = 240 };
        tile.Children.Add(art);
        void Band(Brush b, double y) { var r = new Rectangle { Width = 240, Height = 248 - y, Fill = b }; Canvas.SetTop(r, y - 8); art.Children.Add(r); }
        Canvas Shapes(double k, double x, double y) { var d = new Canvas { RenderTransform = new MatrixTransform { Matrix = new Microsoft.UI.Xaml.Media.Matrix(k, 0, 0, k, x - 8, y - 8) } }; art.Children.Add(d); return d; }
        void Poly(Canvas d, string pts, string hex)
        {
            var p = new Polygon { Fill = new SolidColorBrush(Tint(hex)) };
            foreach (var xy in pts.Split(' ')) { var v = xy.Split(','); p.Points.Add(new Point(double.Parse(v[0]), double.Parse(v[1]))); }
            d.Children.Add(p);
        }
        if (small)
        {
            Band(new SolidColorBrush(Tint("#4FB0DA")), 8);
            Band(new SolidColorBrush(Tint("#0A3F5E")), 98);
            var d = Shapes(204 / 256.0, 26, 24);
            Poly(d, "56,93 56,18 88,46 100,40 171,93", "#DDF7FD");
            Poly(d, "56,93 171,93 222,134 132,190 136,210 56,240", "#2A9ACB");
        }
        else
        {
            Band(Down("#2C8FC0", "#7DCFEA"), 8);
            Band(Down("#0E5F8E", "#062638"), 94);
            var d = Shapes(210 / 256.0, 23, 18);
            Poly(d, "66,38 80,60 66,92", "#F2FDFF");
            Poly(d, "80,60 92,54 106,72 66,92", "#CDF3FA");
            Poly(d, "106,72 155,92 66,92", "#A6E6F2");
            Poly(d, "106,72 116,68 155,92", "#7FD3EA");
            Poly(d, "66,92 155,92 120,124", "#3FB0D8");
            Poly(d, "155,92 212,128 120,124", "#2A9ACB");
            Poly(d, "66,92 120,124 66,160", "#2391C2");
            Poly(d, "120,124 212,128 146,169 150,188 132,179 100,170", "#1A78AA");
            Poly(d, "66,160 120,124 100,170", "#136896");
            Poly(d, "66,160 100,170 132,179 103,197 108,216 92,204 72,232 66,220", "#0C5480");
            var rim = Tint("#7FDCEB"); rim.A = 90;
            var edge = new Border { Width = 240, Height = 240, CornerRadius = new CornerRadius(62), BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(rim) };
            Canvas.SetLeft(edge, 8); Canvas.SetTop(edge, 8);
            canvas.Children.Add(edge);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }
}

// Wheel scrolling that glides to the target instead of stepping (the WPF app's SmoothScroll): each notch moves the
// target 1.6 x the wheel delta, and the view eases there, closing 22% of the gap per 60th of a second. WPF draws at
// 60 fps; WinUI draws at the monitor's rate (160+ Hz here), so the easing goes by time, not by frame: per frame it
// closed the gap 3x too fast and each notch landed as a lurch. scrolling(true/false): while it moves (blobs rest).
static class SmoothScroll
{
    public static readonly Dictionary<ScrollViewer, Action<double>> Wheels = new(); // tests: the test pipe's "wheel"

    public static void Attach(ScrollViewer sv, Action<bool>? scrolling = null)
    {
        double target = 0, cur = 0;
        bool running = false;
        TimeSpan last = default;
        void Tick(object? s, object e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (last == default) { last = now; return; } // the first frame only starts the clock (no guessed step)
            if (now == last) return; // Rendering can fire twice for one frame
            double dt = Math.Clamp((now - last).TotalSeconds, 0, 0.1);
            last = now;
            cur += (target - cur) * (1 - Math.Pow(1 - 0.22, dt * 60));
            if (Math.Abs(target - cur) < 0.5)
            {
                sv.ChangeView(null, target, null, true);
                CompositionTarget.Rendering -= Tick; running = false; scrolling?.Invoke(false);
                return;
            }
            sv.ChangeView(null, cur, null, true);
        }
        void Wheel(double delta)
        {
            if (!running) target = cur = sv.VerticalOffset;
            target = Math.Clamp(target - delta * 1.6, 0, sv.ScrollableHeight);
            if (!running) { last = default; CompositionTarget.Rendering += Tick; running = true; scrolling?.Invoke(true); }
        }
        Wheels[sv] = Wheel;
        // the wheel over the content: taken before the ScrollViewer's own stepping (touchpads still pan natively)
        ((UIElement)sv.Content).PointerWheelChanged += (_, e) =>
        {
            var pt = e.GetCurrentPoint(sv);
            if (pt.Properties.IsHorizontalMouseWheel) return;
            Wheel(pt.Properties.MouseWheelDelta);
            e.Handled = true;
        };
        sv.ViewChanged += (_, _) => { if (!running) target = cur = sv.VerticalOffset; };
    }
}
