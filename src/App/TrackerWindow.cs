using ArnoA2.Tracker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ArnoA2.App;

/// <summary>
/// The always-on-top tracker: merchant suggestions, then pinned and recent (or nearby)
/// pets with their souls, a status line and the session so far. Closing it quits the app.
/// </summary>
public sealed class TrackerWindow : Window
{
    readonly AppState app;
    readonly MapHub maps;
    readonly StackPanel rows = new() { Spacing = 0 };
    readonly TextBlock status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock session = new() { FontSize = 11.5, Foreground = Ui.Muted };
    readonly Border toast = new() { Background = Brush.Parse("#33FFD54F"), BorderBrush = Ui.Gold, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), IsVisible = false };
    readonly Button recent, nearby;
    readonly CheckBox hideMaxed = new() { Content = "Hide maxed" };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    FinderWindow? finder;
    SettingsWindow? settings;
    MiniMapWindow? miniMap;
    FullMapWindow? fullMap;
    string rendered = "";
    bool refreshing;

    public TrackerWindow(AppState app, MapHub maps)
    {
        this.app = app;
        this.maps = maps;
        Title = KdeWindowRules.TrackerTitle;
        Width = 440;
        Height = 440;
        MinWidth = 340;
        MinHeight = 220;
        Topmost = true;
        Background = Ui.Window;

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        actions.Children.Add(Ui.Button("Find a pet", () => Open(ref finder, () => new FinderWindow(app, maps))));
        actions.Children.Add(Ui.Button("Mini-map", OpenMiniMap));
        actions.Children.Add(Ui.Button("Map", OpenFullMap));
        actions.Children.Add(Ui.Button("Settings", () => Open(ref settings, () => new SettingsWindow(app, maps, this))));

        recent = Ui.Button("Recent", () => app.Engine.ChangeSettings(s => s.Mode = "Recent"), "Pets you just earned souls for");
        nearby = Ui.Button("Nearby", () => app.Engine.ChangeSettings(s => s.Mode = "Nearby"), "Pets whose creatures are around you");
        hideMaxed.IsCheckedChanged += (_, _) =>
        {
            if (!refreshing)
                app.Engine.ChangeSettings(s => s.HideMaxed = hideMaxed.IsChecked == true);
        };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        modes.Children.Add(recent);
        modes.Children.Add(nearby);
        modes.Children.Add(hideMaxed);

        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(actions);
        header.Children.Add(modes);
        header.Children.Add(status);
        header.Children.Add(toast);
        var layout = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(session, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(session);
        layout.Children.Add(new ScrollViewer { Content = rows, Margin = new Thickness(0, 6) });
        Content = layout;

        toastTimer.Tick += (_, _) =>
        {
            toast.IsVisible = false;
            toastTimer.Stop();
        };
        app.Changed += Refresh;
        app.Noticed += Notice;
        Closed += (_, _) =>
        {
            foreach (Window? w in new Window?[] { finder, settings, miniMap, fullMap })
                w?.Close();
        };
        Refresh();
    }

    void Open<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window == null)
        {
            var created = create();
            window = created;
            created.Show();
            created.Closed += (_, _) =>
            {
                if (ReferenceEquals(finder, created)) finder = null;
                if (ReferenceEquals(settings, created)) settings = null;
            };
        }

        window.Activate();
    }

    public void OpenMiniMap()
    {
        if (miniMap == null)
        {
            miniMap = new MiniMapWindow(maps);
            miniMap.Closed += (_, _) => miniMap = null;
            miniMap.Show();
        }

        miniMap.Activate();
    }

    public void SetMiniMapShape(bool round) => miniMap?.ApplyShape(round);
    public MiniMapWindow? MiniMap => miniMap;
    public FullMapWindow? FullMap => fullMap;

    public void OpenFullMap()
    {
        if (fullMap == null)
        {
            fullMap = new FullMapWindow(maps);
            fullMap.Closed += (_, _) => fullMap = null;
            fullMap.Show();
        }

        fullMap.Activate();
    }

    void Notice(Notice notice)
    {
        string? text = notice switch
        {
            RankNotice r => $"{GameData.Instance.Pets[r.Pet].Name} reached {(r.Rank == 3 ? "MAX" : "rank " + r.Rank)}",
            GoalNotice g => $"Goal reached: {GameData.Instance.Pets[g.Pet].Name} rank {g.Rank}",
            GapNotice => "Some game data was lost; totals are last-known until your next login",
            _ => null
        };
        if (text == null)
            return;
        toast.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Bright, FontWeight = FontWeight.SemiBold };
        toast.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
        maps.CollectionChanged();
    }

    void Refresh()
    {
        var engine = app.Engine;
        var settings = engine.Settings;
        var now = DateTimeOffset.UtcNow;
        refreshing = true;
        hideMaxed.IsChecked = settings.HideMaxed;
        refreshing = false;
        recent.FontWeight = settings.Mode == "Recent" ? FontWeight.Bold : FontWeight.Normal;
        nearby.FontWeight = settings.Mode == "Nearby" ? FontWeight.Bold : FontWeight.Normal;
        recent.BorderBrush = settings.Mode == "Recent" ? Ui.Gold : null;
        nearby.BorderBrush = settings.Mode == "Nearby" ? Ui.Gold : null;

        (status.Text, status.Foreground) = (app.CaptureProblem, engine.Connection) switch
        {
            (true, _) => (app.CaptureStatus, Ui.Bright),
            (_, Connection.Waiting) => (app.CaptureStatus + " Start the tracker before logging in to sync your soul totals.", Ui.Muted),
            (_, Connection.Identifying) => ("Game found. Recognising your character… (using a skill speeds this up)", Ui.Muted),
            (_, Connection.Unsynced) => ($"{CharacterLabel()} · totals are last-known (≈). Log in with the tracker running to sync.", Ui.Gold),
            _ => ($"{CharacterLabel()} · live", Ui.Good)
        };

        var merchant = engine.MerchantList(now);
        var list = engine.Rows(now);
        string key = string.Join("|", merchant.Select(m => $"{m.Pet}:{m.Price}:{m.Bought}")) + "#" + string.Join("|", list.Select(r => $"{r.Pet}:{r.Progress.ProgressText}:{r.Progress.Rank}:{r.Pinned}:{r.Nearby}:{r.Creature}"));
        session.Text = engine.Session.StartedUtc is { } started
            ? $"Session: {engine.Session.TotalSouls} souls · {engine.Session.Kills} kills · {(int)(now - started).TotalMinutes} min{(engine.Session.Incomplete ? " · incomplete (data lost)" : "")}"
            : "";
        if (key == rendered)
            return;
        rendered = key;
        rows.Children.Clear();
        if (merchant.Count > 0)
        {
            rows.Children.Add(Ui.Heading("Black Cloud Merchant", 15));
            foreach (var offer in merchant)
                rows.Children.Add(new PetRow(engine.Collection.Pets[offer.Pet], offer.Bought ? "Bought — souls received" : null, offer.Bought ? null : offer.Price));
            rows.Children.Add(Ui.Heading(settings.Mode, 15));
        }

        foreach (var row in list)
        {
            string? note = row.Nearby ? row.Creature : row.Creature.Length > 0 ? "Killed " + row.Creature : null;
            rows.Children.Add(new PetRow(row.Progress, note, pinned: row.Pinned));
        }

        if (list.Count == 0)
            rows.Children.Add(new TextBlock
            {
                Text = settings.Mode == "Nearby" ? "No pet creatures in view." : "Pets you earn souls for appear here.",
                Foreground = Ui.Muted,
                Margin = new Thickness(0, 16),
                TextWrapping = TextWrapping.Wrap
            });
    }

    string CharacterLabel() => app.Engine.Character is { Length: > 0 } name ? name + (app.Engine.Assumed ? " (recognised from traffic)" : "") : "Your character";
}
