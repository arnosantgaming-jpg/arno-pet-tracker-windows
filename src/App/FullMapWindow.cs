using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using ArnoA2.App.Map;

namespace ArnoA2.App;

/// <summary>Resizable full region map for a second monitor: target picker, ranked farming
/// spots and live position. F11 toggles fullscreen.</summary>
public sealed class FullMapWindow : Window
{
    readonly MapHub hub;
    readonly MapCanvas canvas;
    readonly ComboBox target = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox pet = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a pet…" };
    readonly ComboBox region = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox follow = new() { Content = "Follow me" };
    readonly CheckBox spawns = new() { Content = "Show all spawn dots" };
    readonly TextBlock position = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 12 };
    readonly TextBlock guidance = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#FFD54F") };
    readonly StackPanel spots = new() { Spacing = 6 };
    readonly TextBlock minimapStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11 };
    string renderedSpots = "";
    bool refreshing;
    static readonly (string Key, string Label)[] TargetLabels =
    {
        ("Unfinished", "All unfinished pets"), ("Pinned", "Pinned pets"), ("Cogni", "Family: Cogni"), ("Fera", "Family: Fera"),
        ("Natura", "Family: Natura"), ("Varian", "Family: Varian"), ("Special", "Family: Special"), ("Pet", "One pet")
    };

    public FullMapWindow(MapHub hub)
    {
        this.hub = hub;
        Title = "Arno's Pet Tracker · map";
        Width = 1280;
        Height = 820;
        MinWidth = 640;
        MinHeight = 420;
        Background = Brush.Parse("#0B1118");
        canvas = new MapCanvas(hub, minimap: false) { Zoom = hub.Preferences.FullZoom };

        var side = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        side.Children.Add(new TextBlock { Text = "Farm for", FontSize = 18 });
        target.ItemsSource = TargetLabels.Select(t => t.Label).ToArray();
        target.SelectedIndex = Math.Max(0, Array.FindIndex(TargetLabels, t => t.Key == hub.Preferences.Target));
        side.Children.Add(target);
        pet.ItemsSource = ArnoA2.Tracker.GameData.Instance.Pets.Values.OrderBy(e => e.Name).Select(e => e.Name).ToArray();
        if (hub.Preferences.PetId is int chosen)
            pet.SelectedItem = ArnoA2.Tracker.GameData.Instance.Pets.GetValueOrDefault(chosen)?.Name;
        side.Children.Add(pet);
        target.SelectionChanged += (_, _) => ChangeTarget();
        pet.SelectionChanged += (_, _) => ChangeTarget();
        side.Children.Add(new TextBlock { Text = "Map", FontSize = 18, Margin = new Thickness(0, 8, 0, 0) });
        region.ItemsSource = new[] { "Verteron", "Altgard" };
        region.SelectedIndex = hub.Region == "altgard" ? 1 : 0;
        region.SelectionChanged += (_, _) =>
        {
            if (refreshing)
                return;
            hub.SetRegion(region.SelectedIndex == 1 ? "altgard" : "verteron");
            canvas.Follow = false;
            canvas.Center = (0.5, 0.5);
        };
        side.Children.Add(region);
        follow.IsChecked = true;
        follow.IsCheckedChanged += (_, _) =>
        {
            if (!refreshing)
                canvas.Follow = follow.IsChecked == true;
        };
        spawns.IsChecked = hub.Preferences.ShowAllSpawns;
        spawns.IsCheckedChanged += (_, _) =>
        {
            hub.Preferences.ShowAllSpawns = spawns.IsChecked == true;
            hub.Save();
        };
        side.Children.Add(follow);
        side.Children.Add(spawns);
        var reader = new CheckBox { Content = "Read in-game minimap (precise position)", IsChecked = hub.Preferences.ReadMinimap };
        reader.IsCheckedChanged += (_, _) =>
        {
            hub.SetMinimapReading(reader.IsChecked == true);
            hub.Save();
            Update();
        };
        side.Children.Add(reader);
        side.Children.Add(Ui.Button("Recalibrate minimap", hub.RecalibrateMinimap));
        side.Children.Add(minimapStatus);
        var fullscreen = Ui.Button("Fullscreen (F11)", ToggleFullscreen);
        side.Children.Add(fullscreen);
        side.Children.Add(position);
        side.Children.Add(guidance);
        side.Children.Add(new TextBlock { Text = "Best spots", FontSize = 18, Margin = new Thickness(0, 8, 0, 0) });
        side.Children.Add(spots);
        side.Children.Add(new TextBlock
        {
            Text = "Spots rank pet spawns you still need by density and distance. Dots are approximate static spawns; bright ringed dots are pet mobs seen live. Drag to pan, scroll to zoom, double-click to follow again, click a numbered spot to target it.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = Brushes.LightSlateGray,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var layout = new DockPanel();
        var scroll = new ScrollViewer { Width = 300, Content = side, Background = Brush.Parse("#0F1720") };
        DockPanel.SetDock(scroll, Dock.Left);
        layout.Children.Add(scroll);
        layout.Children.Add(canvas);
        Content = layout;

        canvas.ViewChanged += () =>
        {
            hub.Preferences.FullZoom = canvas.Zoom;
            refreshing = true;
            follow.IsChecked = canvas.Follow;
            refreshing = false;
            Update();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
                ToggleFullscreen();
            else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
                WindowState = WindowState.Normal;
        };
        hub.Changed += Update;
        Opened += (_, _) =>
        {
            hub.Attach();
            if (hub.Player is { } player)
                canvas.Center = player;
            Update();
        };
        Closed += (_, _) =>
        {
            hub.Changed -= Update;
            hub.Save();
            hub.Detach();
        };
    }

    void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    void ChangeTarget()
    {
        if (refreshing)
            return;
        string key = TargetLabels[Math.Max(0, target.SelectedIndex)].Key;
        int? petId = ArnoA2.Tracker.GameData.Instance.Pets.Values.FirstOrDefault(e => e.Name == pet.SelectedItem as string)?.Id;
        pet.IsVisible = key == "Pet";
        hub.SetTarget(key, petId);
    }

    void Update()
    {
        refreshing = true;
        region.SelectedIndex = hub.Region == "altgard" ? 1 : 0;
        refreshing = false;
        pet.IsVisible = hub.Preferences.Target == "Pet";
        var live = hub.Live;
        position.Text = hub.Error != null ? hub.Error
            : live == null ? "Connecting to capture…"
            : live.Region == null && !hub.Assumed ? (live.Zone == 0 ? "Waiting for a zone: start the tracker, then log in." : $"Zone {live.Zone} has no bundled map.")
            : live.X == null ? "Position unknown until you use a skill, get hit or change zone."
            : (hub.Assumed ? "Map assumed from last session · " : "") + (hub.Source switch { "Minimap" => "From the in-game minimap", "Exact" => "Exact position", _ => "Estimated from nearby mobs" }) + $" · {live.AgeSeconds:F0}s ago · {live.Mobs.Count} pet mobs nearby";
        guidance.Text = hub.Guidance();
        minimapStatus.Text = hub.Preferences.ReadMinimap ? (hub.MinimapStatus.Length > 0 ? hub.MinimapStatus : "Looking for the game minimap…") : "Using network position only.";
        string key = string.Join("|", hub.Hotspots.Select(s => $"{s.X:F4},{s.Y:F4},{s.DistanceMetres:F0}")) + "#" + hub.TargetSpot;
        if (key == renderedSpots)
            return;
        renderedSpots = key;
        spots.Children.Clear();
        for (int i = 0; i < hub.Hotspots.Count; i++)
        {
            var spot = hub.Hotspots[i];
            int index = i;
            string distance = spot.DistanceMetres is double metres ? $"{metres:F0} m" : "";
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{i + 1}. {spot.Spawns} spawns  {distance}", FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = hub.PetNames(spot.PetIds, 4), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.LightSlateGray });
            var button = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderBrush = i == hub.TargetSpot ? Brush.Parse("#FFD54F") : Brushes.Transparent,
                BorderThickness = new Thickness(1)
            };
            button.Click += (_, _) =>
            {
                hub.TargetSpot = index;
                canvas.Frame(spot);
                refreshing = true;
                follow.IsChecked = false;
                refreshing = false;
                Update();
            };
            spots.Children.Add(button);
        }
    }
}
