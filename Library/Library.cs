using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace AnimePlayer;

// Library page extras: favourites, search, filters (favourites / genre), sorting,
// and the background fetch that gives every show its details, banner and (if missing) cover.
public partial class MainWindow
{

    // ---------- favourites (IsFav: LibraryLogic.cs) ----------

    void ToggleFav(object? item)
    {
        if (ToggleFavData(item)) RefreshFavViews();
    }

    void OnStar(object s, RoutedEventArgs e)
    {
        ToggleFav(((FrameworkElement)s).DataContext);
        e.Handled = true;
    }

    void OnDetailStar(object s, RoutedEventArgs e) => ToggleFav(Detail.DataContext);

    // star glyphs update by re-binding (entries are immutable records)
    void RefreshFavViews()
    {
        if (Library.IsVisible) ApplyFilter();
        if (Detail.DataContext is Entry cur)
        {
            DetailStar.Content = IsFav(cur) ? "" : "";
            var list = Videos.ItemsSource;
            Videos.ItemsSource = null;
            Videos.ItemsSource = list;
        }
    }

    // ---------- search / filter / sort ----------

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // the section's shows, sorted once: the filter bar's tag and genre chips come from the same list
        var all = LibraryBase();
        Cards.ItemsSource = Filtered(all, q);
        BuildFilterBar(all);
    }

    // Filtering rebuilds every cover card, so it waits for a short pause in typing.
    System.Windows.Threading.DispatcherTimer? searchDelay;
    void OnSearch(object s, TextChangedEventArgs e)
    {
        if (searchDelay == null)
        {
            searchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
            searchDelay.Tick += (_, _) => { searchDelay.Stop(); if (Library.IsVisible) ApplyFilter(); };
        }
        searchDelay.Stop();
        searchDelay.Start();
    }

    void BuildFilterBar(List<Entry> all)
    {
        FilterBar.Children.Clear();
        Button Chip(string text, bool on, Action click)
        {
            var b = new Button { Style = (Style)FindResource("Chip"), Content = text, Tag = on ? "On" : null, Foreground = (System.Windows.Media.Brush)FindResource("Text"),
                FontSize = 14, Margin = new Thickness(0, 0, 8, 8) };
            System.Windows.Automation.AutomationProperties.SetName(b, $"Filter {text}");
            b.Click += (_, _) => { click(); ApplyFilter(); };
            FilterBar.Children.Add(b);
            return b;
        }
        void Gap() => FilterBar.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 3, 14, 11), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 255, 255, 255)) });

        Chip("All", !filterFav, () => filterFav = false);
        Chip("★ Favourites", filterFav, () => filterFav = true);
        // its tags (Anime / TV shows / Movies / your own), when this library mixes them; click again to show everything
        var tags = FilterTags(all);
        if (tags.Count > 1)
        {
            Gap();
            foreach (var t in tags) Chip(t, kindFilter == t, () => kindFilter = kindFilter == t ? null : t);
        }
        Gap();
        Chip("A–Z", sortMode == "az", () => sortMode = "az");
        Chip("Newest", sortMode == "new", () => sortMode = "new");
        Chip("Top rated", sortMode == "top", () => sortMode = "top");
        // genres present in this section (from fetched details), most common first
        var genres = FilterGenres(all);
        if (genres.Count > 0)
        {
            Gap();
            foreach (var g in genres) Chip(g, genre == g, () => genre = genre == g ? null : g);
        }
    }

    void ResetFilters()
    {
        filterFav = false; genre = null; kindFilter = null; sortMode = "az";
        SearchBox.Text = "";
    }

    // Header search: everything at once, cursor in the search box.
    void OnSearchAll(object s, RoutedEventArgs e)
    {
        currentCategory = "All";
        LibraryTitle.Text = "Everything";
        ResetFilters();
        ApplyFilter();
        Show(Library);
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- background details + covers (PrefetchDetails: LibraryLogic.cs) ----------

    // a show's details came in while you're looking at it
    void OnDetailsFetched(Entry e)
    {
        if (Detail.IsVisible && Detail.DataContext == e) { _ = LoadDetailInfo(e); ShowEpisodes(e, openSeason); }
    }

    void OnDetailsDone() { if (Library.IsVisible) ApplyFilter(); }


}

// Star glyph for an entry or a video: filled when it's a favourite. With parameter "vis": Visible only when favourite.
public sealed class FavConverter : IValueConverter
{
    static readonly System.Windows.Media.Brush Gold = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x3D)));
    static readonly System.Windows.Media.Brush Plain = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));
    static readonly System.Windows.Media.Brush Frost = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }
    public object Convert(object value, Type t, object? p, CultureInfo c)
    {
        bool fav = MainWindow.IsFav(value);
        switch (p as string)
        {
            case "vis": return fav ? Visibility.Visible : Visibility.Collapsed;
            case "color": return fav ? Gold : Plain;                 // gold when favourite, soft white otherwise
            case "fill": return fav ? Gold : Frost;                  // star body: gold, or frosted see-through glass
            case "glow": return fav ? 0.85 : 0.0;
        }
        return fav ? "" : "";
    }
    public object ConvertBack(object v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
