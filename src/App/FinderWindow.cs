using ArnoA2.Tracker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArnoA2.App;

/// <summary>Search pets and their sources, set pins and goals, and see this session.</summary>
public sealed class FinderWindow : Window
{
    readonly AppState app;
    readonly MapHub maps;
    readonly TextBox search = new() { PlaceholderText = "Search pets, creatures, regions or \"merchant\"…" };
    readonly CheckBox hideMaxed = new() { Content = "Hide maxed", IsChecked = true };
    readonly ComboBox family = new() { ItemsSource = new[] { "All families", "Cogni", "Fera", "Natura", "Varian", "Special" }, SelectedIndex = 0 };
    readonly StackPanel results = new();
    readonly TextBlock count = new() { Foreground = Ui.Muted, FontSize = 12 };
    readonly StackPanel sessionPanel = new() { Spacing = 6 };
    string rendered = "";

    public FinderWindow(AppState app, MapHub maps)
    {
        this.app = app;
        this.maps = maps;
        Title = "Arno's Pet Tracker · pets";
        Width = 640;
        Height = 760;
        MinWidth = 440;
        Background = Ui.Window;

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        filters.Children.Add(family);
        filters.Children.Add(hideMaxed);
        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(search);
        top.Children.Add(filters);
        top.Children.Add(count);
        var pets = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        pets.Children.Add(top);
        pets.Children.Add(new ScrollViewer { Content = results });

        var tabs = new TabControl { Margin = new Thickness(14) };
        tabs.Items.Add(new TabItem { Header = "Pets", Content = pets });
        tabs.Items.Add(new TabItem { Header = "Session", Content = new ScrollViewer { Content = sessionPanel } });
        Content = tabs;

        search.TextChanged += (_, _) => Refresh(force: true);
        hideMaxed.IsCheckedChanged += (_, _) => Refresh(force: true);
        family.SelectionChanged += (_, _) => Refresh(force: true);
        app.Changed += Tick;
        Closed += (_, _) => app.Changed -= Tick;
        Refresh(force: true);
    }

    void Tick() => Refresh(force: false);

    IEnumerable<Pet> Matches()
    {
        var data = GameData.Instance;
        var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? onlyFamily = family.SelectedIndex > 0 ? family.SelectedItem as string : null;
        return data.Pets.Values.Where(p =>
        {
            if (onlyFamily != null && p.Family.ToString() != onlyFamily)
                return false;
            if (hideMaxed.IsChecked == true && app.Engine.Collection.Pets[p.Id].Max)
                return false;
            var sources = data.SourcesOfPet.GetValueOrDefault(p.Id) ?? Array.Empty<SoulSource>();
            var how = data.Acquisitions.GetValueOrDefault(p.Id);
            return words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || sources.Any(s => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Region.Contains(w, StringComparison.OrdinalIgnoreCase))
                || how != null && (how.Kind.Contains(w, StringComparison.OrdinalIgnoreCase) || how.Aliases.Any(a => a.Contains(w, StringComparison.OrdinalIgnoreCase))));
        }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    void Refresh(bool force)
    {
        RefreshSession();
        var engine = app.Engine;
        var matches = Matches().Take(80).ToArray();
        string key = (search.Text ?? "") + family.SelectedIndex + hideMaxed.IsChecked + string.Join(",", engine.Settings.Pins) + "|" + string.Join(",", matches.Select(p => engine.Collection.Pets[p.Id].ProgressText + engine.Collection.Pets[p.Id].TargetRank));
        if (!force && key == rendered)
            return;
        rendered = key;
        count.Text = matches.Length == 80 ? "Showing the first 80 matches" : matches.Length == 1 ? "1 pet" : $"{matches.Length} pets";
        results.Children.Clear();
        foreach (var pet in matches)
            results.Children.Add(new PetRow(engine.Collection.Pets[pet.Id], pinned: engine.Settings.Pins.Contains(pet.Id), extra: Details(pet)));
    }

    Control Details(Pet pet)
    {
        var data = GameData.Instance;
        var engine = app.Engine;
        var progress = engine.Collection.Pets[pet.Id];
        var panel = new StackPanel { Spacing = 5 };
        var sources = data.SourcesOfPet.GetValueOrDefault(pet.Id) ?? Array.Empty<SoulSource>();
        if (sources.Count > 0)
            panel.Children.Add(Ui.Note("Souls from: " + string.Join(", ", sources.GroupBy(s => s.Region).Select(g => $"{string.Join(", ", g.Select(s => s.Name).Distinct())} ({g.Key})"))));
        if (data.Acquisitions.GetValueOrDefault(pet.Id) is { } how)
            panel.Children.Add(Ui.Note(how.Kind + (string.IsNullOrWhiteSpace(how.Instructions) ? "" : " · " + how.Instructions)));
        long kills = engine.KillsFor(pet.Id);
        if (kills > 0)
            panel.Children.Add(Ui.Note($"You took part in {kills} kill{(kills == 1 ? "" : "s")} of its creatures."));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bool pinned = engine.Settings.Pins.Contains(pet.Id);
        var pin = Ui.Button(pinned ? "Unpin" : "Pin", () =>
        {
            if (!engine.Pin(pet.Id, !pinned))
                count.Text = "Pin limit reached. Unpin a pet or raise the limit in Settings.";
            Refresh(force: true);
            maps.CollectionChanged();
        }, "Keep this pet at the top of the tracker");
        actions.Children.Add(pin);
        var goal = new ComboBox { ItemsSource = new[] { "No goal", "Goal: rank 1", "Goal: rank 2", "Goal: MAX" }, SelectedIndex = progress.TargetRank ?? 0 };
        goal.SelectionChanged += (_, _) =>
        {
            engine.SetGoal(pet.Id, goal.SelectedIndex == 0 ? null : goal.SelectedIndex);
            Refresh(force: true);
        };
        actions.Children.Add(goal);
        actions.Children.Add(Ui.Button("Farm on map", () =>
        {
            maps.SetTarget("Pet", pet.Id);
            (Owner as TrackerWindow ?? App.Tracker)?.OpenFullMap();
        }));
        panel.Children.Add(actions);
        if (progress.TargetRank is int target)
            panel.Children.Add(new TextBlock
            {
                Text = progress.SoulsToTarget is int left ? left == 0 ? $"Rank {target} goal reached" : $"{left} souls to rank {target}" : $"Goal rank {target} · exact souls after your next synced login",
                FontSize = 11.5,
                Foreground = Ui.Bright
            });
        return panel;
    }

    void RefreshSession()
    {
        var s = app.Engine.Session;
        sessionPanel.Children.Clear();
        sessionPanel.Children.Add(Ui.Heading("This session"));
        if (s.StartedUtc is not { } started)
        {
            sessionPanel.Children.Add(Ui.Note("Souls you earn and pets you rank up appear here."));
            return;
        }

        sessionPanel.Children.Add(new TextBlock { Text = $"{s.TotalSouls} souls · {s.Kills} kills · {s.Rows.Count(r => r.RankUps > 0)} pets ranked up · {(int)(DateTimeOffset.UtcNow - started).TotalMinutes} min", FontWeight = FontWeight.SemiBold });
        if (s.Incomplete)
            sessionPanel.Children.Add(Ui.Note("Some game data was lost during this session, so rewards in that moment are missing."));
        foreach (var row in s.Rows)
            sessionPanel.Children.Add(new TextBlock { Text = $"{GameData.Instance.Pets[row.Pet].Name}: +{row.Souls} souls{(row.RankUps > 0 ? " · ranked up" : "")}" });
        sessionPanel.Children.Add(Ui.Button("Reset session", () =>
        {
            app.Engine.ResetSession();
            RefreshSession();
        }, "Clears this session's totals and the Recent list; your collection is kept"));
    }
}
