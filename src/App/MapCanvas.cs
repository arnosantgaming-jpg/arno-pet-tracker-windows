using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using ArnoA2.App.Map;

namespace ArnoA2.App;

/// <summary>Region map with spawn dots, ranked farming spots, live pet mobs and the player.
/// Coordinates are normalized region units (0..1); Zoom is screen pixels per tile.</summary>
public sealed class MapCanvas : Control
{
    static readonly Dictionary<string, Bitmap?> Tiles = new();
    static readonly IBrush Water = Brush.Parse("#0E1A22");
    static readonly IPen Outline = new Pen(Brushes.White, 1.5);
    static readonly IPen SpotPen = new Pen(Brush.Parse("#FFD54F"), 2);
    static readonly IPen TargetPen = new Pen(Brush.Parse("#FFD54F"), 3);
    static readonly IPen RoutePen = new Pen(Brush.Parse("#CCFFD54F"), 2.5) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
    static readonly IBrush Player = Brush.Parse("#4FC3F7");
    readonly MapHub hub;
    readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    (double X, double Y)? shown;
    Point? drag;
    public MapCanvas(MapHub hub, bool minimap)
    {
        this.hub = hub;
        Minimap = minimap;
        ClipToBounds = true;
        Focusable = true;
        Center = (0.5, 0.5);
        animation.Tick += (_, _) => Animate();
        AttachedToVisualTree += (_, _) => animation.Start();
        DetachedFromVisualTree += (_, _) => animation.Stop();
    }

    public bool Minimap { get; }
    /// <summary>Drawn inside a circle: off-screen markers sit on the circle's edge.</summary>
    public bool Round { get; set; }
    public bool Follow { get; set; } = true;
    public double Zoom { get; set; } = 3;
    public double TileOpacity { get; set; } = 1;
    public (double X, double Y) Center { get; set; }
    /// <summary>Your camera direction, degrees clockwise from north (null = unknown): the
    /// player marker becomes an arrow pointing it.</summary>
    public double? Heading { get; set; }
    /// <summary>Turns the map so the camera direction points up (north up while unknown).</summary>
    public bool TurnMap { get; set; }
    /// <summary>Degrees the map is turned anticlockwise; north sits this far left of up.</summary>
    public double Turned => TurnMap && Heading is double h ? h : 0;
    public event Action? ViewChanged;
    // Pixels per whole-region unit: each tile is drawn at 256 * Zoom px (about 4 m per px at Zoom 1).
    double Pixels => Zoom * 256 * hub.Map.Grid;

    Point Middle => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>Region units to screen: offset from the centre, scaled, then turned about the middle.</summary>
    public Point Screen(double x, double y) => Middle + Turn(new Vector((x - Center.X) * Pixels, (y - Center.Y) * Pixels), -Turned);

    /// <summary>Screen to region units: the inverse of <see cref="Screen"/>.</summary>
    public (double X, double Y) Region(Point p)
    {
        var v = Turn(p - Middle, Turned);
        return (Center.X + v.X / Pixels, Center.Y + v.Y / Pixels);
    }

    /// <summary>A screen vector turned clockwise by the given degrees (y points down).</summary>
    static Vector Turn(Vector v, double degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    void Animate()
    {
        if (hub.Player is { } target)
        {
            shown = shown is { } s && Math.Abs(s.X - target.X) + Math.Abs(s.Y - target.Y) < 0.01 ? (s.X + (target.X - s.X) * 0.3, s.Y + (target.Y - s.Y) * 0.3) : target;
            if (Follow)
                Center = shown.Value;
        }
        else
            shown = null;

        InvalidateVisual();
    }

    static Bitmap? Tile(string region, int column, int row)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "maps", region, $"{column}_{row}.jpg");
        if (Tiles.TryGetValue(path, out var bitmap))
            return bitmap;
        try
        {
            bitmap = File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            bitmap = null;
        }

        return Tiles[path] = bitmap;
    }

    public static IBrush FamilyBrush(int petId) => ArnoA2.Tracker.GameData.Instance.Pets.GetValueOrDefault(petId)?.Family switch
    {
        ArnoA2.Tracker.Family.Cogni => Brush.Parse("#64B5F6"),
        ArnoA2.Tracker.Family.Fera => Brush.Parse("#EF5350"),
        ArnoA2.Tracker.Family.Natura => Brush.Parse("#81C784"),
        ArnoA2.Tracker.Family.Varian => Brush.Parse("#BA68C8"),
        _ => Brush.Parse("#FFD54F")
    };

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Water, bounds);
        var map = hub.Map;
        var region = hub.Spawns;
        double cell = Pixels / map.Grid;
        // Tiles are laid out north up and the whole layer is turned about the middle; any tile
        // within the view's diagonal may turn into sight.
        var middle = Middle;
        double reach = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2;
        var seen = Turned == 0 ? bounds : new Rect(middle.X - reach, middle.Y - reach, 2 * reach, 2 * reach);
        var turn = Matrix.CreateTranslation(-middle.X, -middle.Y) * Matrix.CreateRotation(-Turned * Math.PI / 180) * Matrix.CreateTranslation(middle.X, middle.Y);
        using (context.PushOpacity(TileOpacity))
        using (context.PushTransform(turn))
            foreach (var tile in region.Tiles)
            {
                var start = new Point(middle.X + ((double)tile.Column / map.Grid - Center.X) * Pixels, middle.Y + ((double)tile.Row / map.Grid - Center.Y) * Pixels);
                var rect = new Rect(start, new Size(cell + 0.5, cell + 0.5));
                if (rect.Intersects(seen) && Tile(region.Key, tile.Column, tile.Row) is { } image)
                    context.DrawImage(image, new Rect(image.Size), rect);
            }

        double dot = Math.Clamp(Zoom * 1.2, 2.5, 5);
        if (hub.Preferences.ShowAllSpawns || Minimap)
            foreach (var point in region.Points)
            {
                if (!hub.Weights.ContainsKey(point.PetId))
                    continue;
                var at = Screen(point.X, point.Y);
                if (bounds.Inflate(dot).Contains(at))
                    context.DrawEllipse(FamilyBrush(point.PetId), null, at, dot, dot);
            }

        double radius = FarmingHotspots.RadiusMetres / map.Metres * Pixels;
        for (int i = hub.Hotspots.Count - 1; i >= 0; i--)
        {
            var spot = hub.Hotspots[i];
            var at = Screen(spot.X, spot.Y);
            bool target = i == hub.TargetSpot;
            context.DrawEllipse(target ? Brush.Parse("#33FFD54F") : Brush.Parse("#18FFD54F"), target ? TargetPen : SpotPen, at, Math.Max(radius, 9), Math.Max(radius, 9));
            Label(context, (i + 1).ToString(CultureInfo.InvariantCulture), at, 13, Brushes.Black, Brush.Parse("#FFD54F"));
        }

        if (hub.Live is { } live && live.Region == hub.Region)
            foreach (var mob in live.Mobs)
            {
                var (x, y) = map.Normalize(mob.X, mob.Y);
                var at = Screen(x, y);
                if (!bounds.Contains(at))
                    continue;
                bool wanted = hub.Weights.ContainsKey(mob.PetId);
                context.DrawEllipse(wanted ? FamilyBrush(mob.PetId) : Brush.Parse("#90A4AE"), new Pen(wanted ? Brushes.White : Brushes.Black, wanted ? 2 : 1), at, wanted ? 6 : 4, wanted ? 6 : 4);
            }

        if (shown is { } player)
        {
            var at = Screen(player.X, player.Y);
            if (hub.Target is { } goal && (goal.DistanceMetres ?? 0) >= FarmingHotspots.RadiusMetres)
            {
                var end = Screen(goal.X, goal.Y);
                context.DrawLine(RoutePen, at, end);
                if (Round)
                {
                    double edge = Math.Min(bounds.Width, bounds.Height) / 2 - 16;
                    if (Distance(end, middle) > edge)
                        RoundEdgeArrow(context, at, end, middle, edge);
                }
                else if (!bounds.Deflate(14).Contains(end))
                    EdgeArrow(context, at, end, bounds);
            }

            bool exact = hub.Precise;
            if (!exact)
                context.DrawEllipse(Brush.Parse("#224FC3F7"), new Pen(Brush.Parse("#664FC3F7"), 1), at, Math.Max(10 / map.Metres * Pixels, 12), Math.Max(10 / map.Metres * Pixels, 12));
            context.DrawEllipse(Brush.Parse("#554FC3F7"), null, at, 14, 14);
            if (Heading is double facing)
                // An arrow pointing where the camera looks (read from the game's minimap cone;
                // the network gives no facing). Up when the map turns with it.
                Facing(context, at, facing - Turned);
            else
            {
                // A plain dot while the camera is not read; a heading guessed from movement
                // spun around on every small correction.
                // White centre and thick blue ring keep it distinct from the coloured mob dots.
                context.DrawEllipse(Brushes.White, new Pen(Brush.Parse("#0B1118"), 1), at, 9, 9);
                context.DrawEllipse(Brushes.White, new Pen(Player, 3.5), at, 6, 6);
            }
        }
        else if (Minimap)
            Label(context, hub.Live == null ? "Connecting…" : Round ? "Position unknown" : "Position unknown · use a skill or relog", new Point(bounds.Width / 2, bounds.Height / 2), 12, Brushes.White, Brush.Parse("#AA000000"));
    }

    /// <summary>The player arrow: blue with a white rim, pointing the given degrees clockwise from up.</summary>
    static void Facing(DrawingContext context, Point at, double degrees)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(0, -12), true);
            g.LineTo(new Point(8.5, 8));
            g.LineTo(new Point(0, 3.5));
            g.LineTo(new Point(-8.5, 8));
            g.EndFigure(true);
        }

        using (context.PushTransform(Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawGeometry(null, new Pen(Brush.Parse("#0B1118"), 4.5) { LineJoin = PenLineJoin.Round }, shape);
            context.DrawGeometry(Player, new Pen(Brushes.White, 2) { LineJoin = PenLineJoin.Round }, shape);
        }
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Arrow where the line from the player toward the target leaves the circle.</summary>
    static void RoundEdgeArrow(DrawingContext context, Point from, Point to, Point middle, double radius)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;
        dx /= length;
        dy /= length;
        // Solve |from + t·d − middle| = radius for the exit point (t > 0).
        double fx = from.X - middle.X, fy = from.Y - middle.Y, b = fx * dx + fy * dy, c = fx * fx + fy * fy - radius * radius;
        double t = -b + Math.Sqrt(Math.Max(0, b * b - c));
        var tip = new Point(from.X + dx * t, from.Y + dy * t);
        Arrow(context, tip, Math.Atan2(dy, dx));
    }

    static void Arrow(DrawingContext context, Point tip, double angle)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(10, 0), true);
            g.LineTo(new Point(-7, 7));
            g.LineTo(new Point(-7, -7));
            g.EndFigure(true);
        }

        using (context.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(tip.X, tip.Y)))
            context.DrawGeometry(Brush.Parse("#FFD54F"), new Pen(Brushes.Black, 1.5), shape);
    }

    static void EdgeArrow(DrawingContext context, Point from, Point to, Rect bounds)
    {
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var inner = bounds.Deflate(16);
        double tx = Math.Cos(angle), ty = Math.Sin(angle);
        double scale = Math.Min(Math.Abs((tx > 0 ? inner.Right - from.X : inner.Left - from.X) / (tx == 0 ? 1e-9 : tx)), Math.Abs((ty > 0 ? inner.Bottom - from.Y : inner.Top - from.Y) / (ty == 0 ? 1e-9 : ty)));
        Arrow(context, new Point(from.X + tx * scale, from.Y + ty * scale), angle);
    }

    static void Label(DrawingContext context, string text, Point center, double size, IBrush foreground, IBrush background)
    {
        var format = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Noto Sans", FontStyle.Normal, FontWeight.Bold), size, foreground);
        var box = new Rect(center.X - format.Width / 2 - 4, center.Y - format.Height / 2 - 1, format.Width + 8, format.Height + 2);
        context.DrawRectangle(background, null, box, 4, 4);
        context.DrawText(format, new Point(box.X + 4, box.Y + 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            Follow = true;
            ViewChanged?.Invoke();
            return;
        }

        var point = e.GetPosition(this);
        // Clicking a numbered spot makes it the guidance target.
        double radius = Math.Max(FarmingHotspots.RadiusMetres / hub.Map.Metres * Pixels, 12);
        for (int i = 0; i < hub.Hotspots.Count; i++)
        {
            var at = Screen(hub.Hotspots[i].X, hub.Hotspots[i].Y);
            if (Math.Pow(at.X - point.X, 2) + Math.Pow(at.Y - point.Y, 2) <= radius * radius)
            {
                hub.TargetSpot = i;
                ViewChanged?.Invoke();
                return;
            }
        }

        drag = point;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag is not { } start)
            return;
        var point = e.GetPosition(this);
        // Through Region, so the map follows the pointer when it is turned too.
        var (from, to) = (Region(start), Region(point));
        Center = (Center.X + from.X - to.X, Center.Y + from.Y - to.Y);
        drag = point;
        if (Follow)
        {
            Follow = false;
            ViewChanged?.Invoke();
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    public void ZoomAt(Point? anchor, double factor)
    {
        var point = anchor ?? Middle;
        var before = Region(point);
        Zoom = Math.Clamp(Zoom * factor, 0.15, 24);
        if (!Follow)
        {
            var after = Region(point);
            Center = (Center.X + before.X - after.X, Center.Y + before.Y - after.Y);
        }

        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void Frame(Hotspot spot)
    {
        Follow = false;
        Center = (spot.X, spot.Y);
        InvalidateVisual();
    }
}
