using ArnoA2.Tracker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArnoA2.App;

public sealed class SettingsWindow : Window
{
    readonly AppState app;
    readonly TextBlock captureNote = Ui.Note("");

    public SettingsWindow(AppState app, MapHub maps, TrackerWindow tracker)
    {
        this.app = app;
        Title = "Arno's Pet Tracker · settings";
        Width = 500;
        Height = 720;
        Background = Ui.Window;
        var engine = app.Engine;
        var s = engine.Settings;
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };

        panel.Children.Add(Ui.Heading("Tracking"));
        panel.Children.Add(Choice("Order", new[] { "Unfinished first", "Recent only", "Closest to next rank" }, s.Sort, v => engine.ChangeSettings(x => x.Sort = v)));
        panel.Children.Add(Number("Rows shown", s.RowLimit, 1, 20, v => engine.ChangeSettings(x => x.RowLimit = v)));
        panel.Children.Add(Toggle("Hide recent pets after a while", s.ExpireInactive, v => engine.ChangeSettings(x => x.ExpireInactive = v)));
        panel.Children.Add(Number("…after seconds", s.InactiveSeconds, 5, 600, v => engine.ChangeSettings(x => x.InactiveSeconds = v)));
        panel.Children.Add(Number("Nearby pets stay for seconds", s.NearbyStaleSeconds, 10, 600, v => engine.ChangeSettings(x => x.NearbyStaleSeconds = v)));
        panel.Children.Add(Number("Pinned pets (max)", s.PinLimit, 1, 5, v => engine.ChangeSettings(x => x.PinLimit = v)));
        panel.Children.Add(Toggle("Black Cloud Merchant suggestions", s.MerchantSuggestions, v => engine.ChangeSettings(x => x.MerchantSuggestions = v)));
        panel.Children.Add(Toggle("New characters share this account's collection", s.NewCharactersJoin, v => engine.ChangeSettings(x => x.NewCharactersJoin = v)));
        panel.Children.Add(Ui.Note("Pet collections are account-wide in AION 2. Turn this off only if you play several accounts."));

        panel.Children.Add(Ui.Heading("Mini-map"));
        panel.Children.Add(Choice("Shape", new[] { "Circle", "Rectangle" }, maps.Preferences.MiniShape == "Rectangle" ? "Rectangle" : "Circle", v =>
        {
            maps.Preferences.MiniShape = v == "Rectangle" ? "Rectangle" : "Round";
            maps.Save();
            tracker.SetMiniMapShape(v != "Rectangle");
        }));
        panel.Children.Add(Toggle("Read the in-game minimap for a precise position", maps.Preferences.ReadMinimap, v =>
        {
            maps.SetMinimapReading(v);
            maps.Save();
        }));
        panel.Children.Add(Ui.Note("Reads only the minimap corner of the game window (about 5% of one CPU core). Verteron and Altgard."));

        panel.Children.Add(Ui.Heading("Account"));
        var name = new TextBox { Text = engine.Profile.Name };
        name.LostFocus += (_, _) =>
        {
            engine.Profile.Name = string.IsNullOrWhiteSpace(name.Text) ? "My account" : name.Text.Trim();
            engine.ChangeSettings(_ => { });
        };
        panel.Children.Add(name);
        panel.Children.Add(Ui.Note(engine.Profile.Characters.Count > 0 ? "Characters: " + string.Join(", ", engine.Profile.Characters) : "No characters seen yet."));

        panel.Children.Add(Ui.Heading("Capture"));
        panel.Children.Add(captureNote);
        string daemon = Path.Combine(AppContext.BaseDirectory, "capture", "arnoa2-capture");
        panel.Children.Add(Ui.Button("Grant capture permission", async () =>
        {
            captureNote.Text = "Waiting for the password dialog…";
            try
            {
                captureNote.Text = await AppState.GrantPermission(daemon);
            }
            catch (Exception ex)
            {
                captureNote.Text = "Could not grant: " + ex.Message;
            }
        }, "Gives the capture program (only) permission to read network packets; asks for your password"));
        if (KdeWindowRules.Available)
        {
            var kdeNote = Ui.Note("");
            try
            {
                kdeNote.Text = KdeWindowRules.Installed() ? "KDE window rules are installed." : "Adds two KDE window rules so the tracker and mini-map stay above a fullscreen game.";
            }
            catch (Exception)
            {
            }

            panel.Children.Add(Ui.Button("Keep tracker and mini-map above the game (KDE)", () =>
            {
                try
                {
                    KdeWindowRules.Install();
                    kdeNote.Text = "Added. Reopen the mini-map if it is still behind the game. Remove them in System Settings → Window Management → Window Rules.";
                }
                catch (Exception ex)
                {
                    kdeNote.Text = "Could not add the rules: " + ex.Message;
                }
            }));
            panel.Children.Add(kdeNote);
        }

        panel.Children.Add(Ui.Heading("About"));
        panel.Children.Add(Ui.Note($"Data: {app.Store.Folder}\nArno's Pet Tracker is unofficial and not affiliated with NCSOFT; using third-party tools can put your account at risk. Message formats and data: 0Fox2Give/PetTracker and SkeeveAN/Aion-DPS-Meter. GPL-3.0."));

        Content = new ScrollViewer { Content = panel };
        app.Changed += Update;
        Closed += (_, _) => app.Changed -= Update;
        Update();
    }

    void Update() => captureNote.Text = captureNote.Text is { Length: > 0 } t && (t.StartsWith("Permission", StringComparison.Ordinal) || t.StartsWith("Not granted", StringComparison.Ordinal) || t.StartsWith("Waiting for the password", StringComparison.Ordinal)) ? t : app.CaptureStatus;

    static Control Toggle(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    static Control Number(string label, int value, int min, int max, Action<int> changed)
    {
        var input = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 130 };
        input.ValueChanged += (_, _) =>
        {
            if (input.Value is decimal v)
                changed((int)v);
        };
        return Row(label, input);
    }

    static Control Choice(string label, string[] options, string value, Action<string> changed)
    {
        var box = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(value) ? value : options[0], Width = 200 };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string v)
                changed(v);
        };
        return Row(label, box);
    }

    static Control Row(string label, Control input)
    {
        var row = new DockPanel();
        DockPanel.SetDock(input, Dock.Right);
        row.Children.Add(input);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
