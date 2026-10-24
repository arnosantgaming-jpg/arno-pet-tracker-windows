using ArnoA2.Tracker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ArnoA2.App;

/// <summary>Colours and small builders shared by the windows.</summary>
public static class Ui
{
    public static readonly IBrush Window = Brush.Parse("#0B1118");
    public static readonly IBrush Panel = Brush.Parse("#121B25");
    public static readonly IBrush Line = Brush.Parse("#263241");
    public static readonly IBrush Gold = Brush.Parse("#C9A227");
    public static readonly IBrush Bright = Brush.Parse("#FFD54F");
    public static readonly IBrush Muted = Brush.Parse("#8A9AAE");
    public static readonly IBrush Good = Brush.Parse("#81C784");
    public static readonly IBrush Track = Brush.Parse("#2E3A48");

    public static Button Button(string label, Action action, string? tip = null)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 5), CornerRadius = new CornerRadius(6) };
        if (tip != null)
            ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    public static TextBlock Heading(string text, double size = 18) => new() { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
    public static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    static readonly Dictionary<string, Bitmap?> images = new();

    public static Image Picture(string relative, double size)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        if (!images.TryGetValue(path, out var bitmap))
        {
            try
            {
                bitmap = File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception)
            {
                bitmap = null;
            }

            images[path] = bitmap;
        }

        return new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform };
    }
}

/// <summary>One pet: portrait, name, rank and family, souls toward the next rank and a bar.</summary>
public sealed class PetRow : Border
{
    public PetRow(PetProgress progress, string? note = null, long? price = null, bool pinned = false, Control? extra = null)
    {
        var pet = GameData.Instance.Pets[progress.Pet];
        Padding = new Thickness(0, 9);
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = Ui.Line;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*"), ColumnSpacing = 10 };
        grid.Children.Add(Ui.Picture($"portraits/{pet.Id}.png", 44));
        var body = new StackPanel { Spacing = 4 };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var title = new DockPanel();
        var amount = new TextBlock { Text = progress.ProgressText, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = progress.Max ? Ui.Good : Brushes.White };
        DockPanel.SetDock(amount, Dock.Right);
        title.Children.Add(amount);
        title.Children.Add(new TextBlock { Text = (pinned ? "★ " : "") + pet.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(title);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock { Text = progress.Rank is int rank ? (rank == 0 ? "Not owned" : $"Rank {rank}") : "Rank ?", Foreground = Ui.Muted, FontSize = 12 });
        if (pet.Family is not Family.Unknown)
        {
            line.Children.Add(Ui.Picture($"crystals/{pet.Family.ToString().ToLowerInvariant()}.png", 15));
            line.Children.Add(new TextBlock { Text = pet.Family.ToString(), FontSize = 11, Foreground = MapCanvas.FamilyBrush(pet.Id), VerticalAlignment = VerticalAlignment.Center });
        }

        body.Children.Add(line);
        bool unknown = !progress.Max && progress.Balance == null && progress.Requirement != null;
        body.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = progress.Fraction,
            Height = 7,
            MinHeight = 7,
            CornerRadius = new CornerRadius(4),
            Background = unknown ? Brush.Parse("#3A3F2A") : Ui.Track,
            Foreground = progress.Max ? Ui.Good : progress.Certainty == Certainty.Exact ? Ui.Bright : Ui.Gold
        });
        if (progress.RankUpReady)
            body.Children.Add(new TextBlock { Text = progress.Rank == 0 ? "Ready to unlock in game" : "Ready to rank up in game", FontSize = 11, Foreground = Ui.Bright });
        else if (unknown)
            body.Children.Add(new TextBlock { Text = "Soul total unknown · log in with the tracker running to sync", FontSize = 11, Foreground = Ui.Gold, TextWrapping = TextWrapping.Wrap });
        if (price is long kina)
            body.Children.Add(new TextBlock { Text = $"Black Cloud Merchant · {kina:N0} Kina", FontSize = 12, Foreground = Ui.Bright });
        if (!string.IsNullOrEmpty(note))
            body.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        if (extra != null)
            body.Children.Add(extra);
        Child = grid;
    }
}
