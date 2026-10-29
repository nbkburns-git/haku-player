using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// "Wrong show info?" on a show page: search AniList and pick the right entry. The pick is remembered per folder
// (Settings.InfoOverrides) and always wins over automatic matching.
public partial class MainWindow
{
    Entry? fixing;
    int fixSeq;

    void OnFixOpen(object s, RoutedEventArgs e)
    {
        if (Detail.DataContext is not Entry entry) return;
        fixing = entry;
        FixSearch.Text = entry.Name;
        FixLayer.Visibility = Visibility.Visible;
        FixLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        // blur the page behind the glass while open, like Settings (keeps the see-through panel readable)
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        Content.Effect = blur;
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(36, TimeSpan.FromMilliseconds(220)));
        FixSearch.Focus();
        FixSearch.SelectAll();
        _ = RunFixSearch();
    }

    void CloseFix()
    {
        fixing = null;
        fixSeq++;
        FixLayer.Visibility = Visibility.Collapsed;
        FixResults.Children.Clear();
        Content.Effect = null;
    }

    void OnFixClose(object s, RoutedEventArgs e) => CloseFix();
    void OnFixBackdrop(object s, MouseButtonEventArgs e) { if (e.OriginalSource == FixLayer) CloseFix(); }

    void OnFixKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = RunFixSearch(); e.Handled = true; }
    }

    async Task RunFixSearch()
    {
        var text = FixSearch.Text.Trim();
        if (text.Length == 0 || fixing is not Entry entry) return;
        int my = ++fixSeq;
        Status("Searching AniList…");
        System.Collections.Generic.List<ShowChoice> results;
        try { results = await InfoService.Choices(text, entry.Kind); }
        catch { results = new(); }
        if (my != fixSeq) return; // a newer search started
        FixResults.Children.Clear();
        if (results.Count == 0) { Status("Nothing found. Try the Japanese (romaji) name, or fewer words."); return; }
        FixStatus.Visibility = Visibility.Collapsed;
        var now = InfoService.Cached(entry.InfoKey);
        string? current = now?.AniListId?.ToString() ?? now?.ImdbId;
        foreach (var r in results) FixResults.Children.Add(ChoiceRow(r, r.Id == current));
    }

    void Status(string text) { FixStatus.Text = text; FixStatus.Visibility = Visibility.Visible; }

    Button ChoiceRow(ShowChoice r, bool isCurrent)
    {
        var cover = new Border { Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0), Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) };
        if (r.Cover != null)
            try { cover.Background = new ImageBrush(new BitmapImage(new Uri(r.Cover))) { Stretch = Stretch.UniformToFill }; } catch { }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (r.Other != null) text.Children.Add(new TextBlock { Text = r.Other, FontSize = 13, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var facts = string.Join(" · ", new[] { r.Format, r.Year?.ToString(), r.Episodes is int n ? $"{n} episode{(n == 1 ? "" : "s")}" : null }.Where(x => x != null));
        text.Children.Add(new TextBlock { Text = facts, FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        var row = new DockPanel();
        if (isCurrent)
        {
            var tag = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), Child = new TextBlock { Text = "Current", FontSize = 12 } };
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
        }
        row.Children.Add(cover);
        row.Children.Add(text);
        var b = new Button { Style = (Style)FindResource("Row"), Content = row, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = (Brush)FindResource("Text") };
        System.Windows.Automation.AutomationProperties.SetName(b, $"Pick {r.Title} ({r.Year})");
        b.Click += async (_, _) => await Pick(r);
        return b;
    }

    async Task Pick(ShowChoice r)
    {
        if (fixing is not Entry entry) return;
        if (entry.Kind == "anime") Settings.Current.InfoOverrides[entry.Folder] = int.Parse(r.Id);
        else Settings.Current.MediaOverrides[entry.Folder] = r.Id;
        Settings.Current.Save();
        FixResults.Children.Clear();
        Status($"Getting details for {r.Title}…");
        var info = await InfoService.Refresh(entry);
        CloseFix();
        if (Detail.DataContext == entry) await LoadDetailInfo(entry);
        bool got = (info?.AniListId?.ToString() ?? info?.ImdbId) == r.Id;
        if (got && entry.Kind != "anime" && info?.CoverUrl != null && await SaveCover(entry, info.CoverUrl, replace: true)) await SyncLibrary(false); // new poster too
        Toast(got ? $"Details updated · {r.Title}" : "Couldn't reach the details service; your pick is saved and will be used next time");
    }
}
