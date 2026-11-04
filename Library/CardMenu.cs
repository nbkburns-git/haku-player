using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AnimePlayer;

// Right-click a show (library card, Continue watching card, or the title on its page): play, rename, favourite,
// mark watched, fix its details, open its folder. Rename only changes what Haku Player shows (Settings.Renames);
// the folder on disk keeps its name, and matching and cached details still use the folder's name.
public partial class MainWindow
{
    Popup? cardMenu;

    void OnCardMenu(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is Entry entry) ShowCardMenu(entry);
        e.Handled = true;
    }

    void OnDetailMenu(object s, MouseButtonEventArgs e)
    {
        if (Detail.DataContext is Entry entry) ShowCardMenu(entry);
        e.Handled = true;
    }

    // where Play starts: the episode you're part-way through, else the next unwatched one, else the first
    static int ResumeIndex(Entry e)
    {
        int bi = -1; WatchRec? best = null;
        for (int i = 0; i < e.Videos.Count; i++)
            if (Watch.Get(e.Videos[i].Path) is { } r && (best == null || r.Last > best.Last)) { best = r; bi = i; }
        if (best == null || e.Unit == "themes") return 0;
        if (!best.Done) return bi;
        int next = e.Videos.FindIndex(bi + 1, v => v.Season >= 0 && !Watch.IsDone(v.Path));
        return next >= 0 ? next : 0;
    }

    void ShowCardMenu(Entry entry)
    {
        if (entry.IsRandom) return;
        cardMenu?.SetCurrentValue(Popup.IsOpenProperty, false);
        var items = new StackPanel { MinWidth = 250 };
        var popup = new Popup
        {
            Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new ContentControl { Style = (Style)FindResource("GlassPanel"), Margin = new Thickness(10), Padding = new Thickness(6), Content = items },
        };
        cardMenu = popup;

        void Close() => popup.IsOpen = false;
        void Item(string icon, string text, Action act, string? hint = null)
        {
            var row = new DockPanel { LastChildFill = true };
            if (hint != null) { var h = new TextBlock { Text = hint, FontSize = 12.5, Opacity = 0.55, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(h, Dock.Right); row.Children.Add(h); }
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 },
                    new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) },
                },
            });
            var b = new Button { Style = (Style)FindResource("Glass"), Content = row, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 1, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            b.Click += (_, _) => { Close(); act(); };
            items.Children.Add(b);
        }
        void Line() => items.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)) });

        int at = ResumeIndex(entry);
        bool mid = Watch.Get(entry.Videos.ElementAtOrDefault(at)?.Path ?? "") is { Done: false, Pos: > 5 };
        string what = entry.Kind == "movie" || entry.Videos.Count <= at ? "" : entry.Videos[at].Label;
        Item("\uE768", mid ? "Resume" : "Play", () => { returnTo = Detail.IsVisible ? Detail : Library.IsVisible ? Library : Home; Play(entry, at); }, what.Length > 18 ? what[..17] + "…" : what);
        if (!Detail.IsVisible || Detail.DataContext != entry) Item("\uE8A7", "Open", () => OpenShow(entry));
        Line();
        Item("\uE8AC", "Rename…", () => Dispatcher.BeginInvoke(() => ShowRename(entry)), "in the app only");
        if (entry.Title != entry.Name) Item("\uE7A7", $"Use the folder's name", () => SetRename(entry, null));
        Item(IsFav(entry) ? "\uE735" : "\uE734", IsFav(entry) ? "Remove from favourites" : "Add to favourites", () => ToggleFav(entry));
        if (entry.Unit != "themes")
        {
            var eps = entry.Videos.Where(v => v.Season >= 0).ToList();
            bool all = eps.Count > 0 && eps.All(v => Watch.IsDone(v.Path));
            if (eps.Count > 0) Item(all ? "\uE7A7" : "\uE73E", all ? "Mark as unwatched" : "Mark all watched", () => Watch.SetDone(eps.Select(v => v.Path), !all));
            Item("\uE8AB", "Wrong show info?", () => { OpenShow(entry); OnFixOpen(this, new RoutedEventArgs()); });
        }
        Line();
        Item("\uE838", "Show in File Explorer", () => { try { Process.Start("explorer.exe", $"/select,\"{entry.Folder.TrimEnd('\\')}\""); } catch { } });
        popup.IsOpen = true;
    }

    // a small glass box at the mouse: type a name, Enter saves, Esc cancels, empty goes back to the folder's name
    void ShowRename(Entry entry)
    {
        var box = new TextBox
        {
            Text = entry.Title, FontSize = 16, MinWidth = 320, Padding = new Thickness(10, 7, 10, 7), Foreground = (Brush)FindResource("Text"), CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "New name");
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Rename", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) },
                box,
                new TextBlock { Text = $"Only in Haku Player: the folder stays \"{System.IO.Path.GetFileName(entry.Folder.TrimEnd('\\'))}\". Enter to save, Esc to cancel, empty for the folder's name.",
                                FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(2, 8, 0, 0) },
            },
        };
        var popup = new Popup
        {
            Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new ContentControl { Style = (Style)FindResource("GlassPanel"), Margin = new Thickness(10), Padding = new Thickness(16, 14, 16, 14), Content = panel },
        };
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) { popup.IsOpen = false; SetRename(entry, box.Text); k.Handled = true; }
            else if (k.Key == Key.Escape) { popup.IsOpen = false; k.Handled = true; }
        };
        popup.Opened += (_, _) => { box.Focus(); Keyboard.Focus(box); box.SelectAll(); };
        popup.IsOpen = true;
    }

    void SetRename(Entry entry, string? name)
    {
        name = name?.Trim() ?? "";
        var s = Settings.Current;
        if (name.Length == 0 || name == entry.Name) s.Renames.Remove(entry.Folder); else s.Renames[entry.Folder] = name;
        s.Save();
        // entries are immutable records showing a computed Title: re-bind whatever shows this one
        if (Library.IsVisible) ApplyFilter();
        if (Detail.DataContext is Entry cur && cur.Folder == entry.Folder) { Detail.DataContext = null; Detail.DataContext = cur; }
        if (Home.IsVisible) UpdateContinue();
        Toast(name.Length == 0 || name == entry.Name ? $"Back to the folder's name · {entry.Name}" : $"Renamed to {name} (the folder is unchanged)");
    }
}
