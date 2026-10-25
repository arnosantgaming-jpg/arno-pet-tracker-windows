using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArnoA2.App;

/// <summary>
/// Borderless overlay map, round or rectangular: the player in the middle (north up), a
/// frame with compass marks, and the next farming spot as a line or an arrow on the frame.
/// Drag the frame to move it, drag inside to pan; controls appear on hover.
/// </summary>
public sealed class MiniMapWindow : Window
{
    public const double Frame = 14, Footer = 30;
    readonly MapHub hub;
    readonly MapCanvas canvas;
    readonly Grid face = new();
    readonly FrameControl frame = new();
    readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
    readonly Border opacityPanel;
    readonly GoldSlider opacity = new() { Minimum = 0.2, Maximum = 1, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    // Scrolls sideways when the guidance is longer than the label.
    readonly MarqueeText guidance = new() { FontSize = 11.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border grip;
    readonly Button shapeButton;
    readonly Border label;
    bool sizing;

    public MiniMapWindow(MapHub hub)
    {
        this.hub = hub;
        var preferences = hub.Preferences;
        Title = KdeWindowRules.MiniMapTitle;
        Width = Math.Clamp(preferences.MiniWidth, 160, 1200);
        Height = Math.Clamp(preferences.MiniHeight, 160 + Footer, 1200);
        MinWidth = 160;
        MinHeight = 160 + Footer;
        Topmost = true;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        if (preferences.MiniX is int x && preferences.MiniY is int y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        canvas = new MapCanvas(hub, minimap: true) { Zoom = preferences.MiniZoom, Margin = new Thickness(Frame) };
        opacity.Value = preferences.MiniOpacity;
        opacity.ValueChanged += value =>
        {
            preferences.MiniOpacity = value;
            Fade(value);
        };

        shapeButton = Small("◯", "Switch between circle and rectangle", ToggleShape);
        foreach (var button in new[]
        {
            Small("−", "Zoom out", () => canvas.ZoomAt(null, 0.8)),
            Small("+", "Zoom in", () => canvas.ZoomAt(null, 1.25)),
            Small("◎", "Centre on me", () =>
            {
                canvas.Follow = true;
                canvas.InvalidateVisual();
            }),
            Small("⟳", "Next farming spot", () =>
            {
                if (hub.Hotspots.Count > 0)
                    hub.TargetSpot = (hub.TargetSpot + 1) % hub.Hotspots.Count;
                Update();
            }),
            shapeButton,
            Small("✕", "Close the mini-map", Close)
        })
            controls.Children.Add(button);
        controls.Margin = new Thickness(0, 0, 0, Frame + 10);

        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        opacityRow.Children.Add(new TextBlock { Text = "Opacity", FontSize = 10.5, Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        opacityRow.Children.Add(opacity);
        opacityPanel = new Border { Background = Brush.Parse("#CC0B1118"), Padding = new Thickness(12, 0), Height = 28, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Frame + 10, 0, 0), Child = opacityRow, IsVisible = false };

        grip = new Border
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            IsVisible = false,
            Child = new TextBlock { Text = "◢", FontSize = 13, Foreground = Ui.Gold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom }
        };
        grip.PointerPressed += (_, e) =>
        {
            sizing = true;
            BeginResizeDrag(WindowEdge.SouthEast, e);
        };

        face.Children.Add(canvas);
        face.Children.Add(frame);
        face.Children.Add(opacityPanel);
        face.Children.Add(controls);
        face.Children.Add(grip);
        // Pressing on the frame (not the map) moves the window.
        frame.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        label = new Border { Background = Brush.Parse("#D90B1118"), BorderBrush = Brush.Parse("#80C9A227"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 2), Height = 24, HorizontalAlignment = HorizontalAlignment.Center, Child = guidance, Margin = new Thickness(8, 4, 8, 0) };
        var layout = new DockPanel();
        DockPanel.SetDock(label, Dock.Bottom);
        layout.Children.Add(label);
        layout.Children.Add(face);
        Content = layout;

        Fade(preferences.MiniOpacity);
        PointerEntered += (_, _) => ShowControls(true);
        PointerExited += (_, _) => ShowControls(false);
        PointerReleased += (_, _) => sizing = false;
        ApplyShape(preferences.MiniShape != "Rectangle");

        canvas.ViewChanged += () => preferences.MiniZoom = canvas.Zoom;
        hub.Changed += Update;
        Opened += (_, _) =>
        {
            hub.Attach();
            Update();
        };
        Closing += (_, _) =>
        {
            preferences.MiniX = Position.X;
            preferences.MiniY = Position.Y;
            preferences.MiniWidth = Width;
            preferences.MiniHeight = Height;
            hub.Save();
        };
        Closed += (_, _) =>
        {
            hub.Changed -= Update;
            hub.Detach();
        };
    }

    bool Round => frame.Round;

    void ToggleShape()
    {
        ApplyShape(!Round);
        hub.Preferences.MiniShape = Round ? "Round" : "Rectangle";
        hub.Save();
    }

    public void ApplyShape(bool round)
    {
        frame.Round = round;
        canvas.Round = round;
        shapeButton.Content = round ? "▢" : "◯";
        if (round)
            Height = Width + Footer; // a circle needs a square face
        Layout(new Size(Width, Height));
        frame.InvalidateVisual();
        canvas.InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Round && sizing && Math.Abs(e.NewSize.Height - (e.NewSize.Width + Footer)) >= 1)
            Height = e.NewSize.Width + Footer; // keep the circle round while resizing
        Layout(e.NewSize);
    }

    void Layout(Size size)
    {
        double w = size.Width, h = Math.Max(size.Height - Footer, 0);
        if (Round)
            w = h = Math.Min(w, h);
        face.Width = Math.Max(w, 0);
        face.Height = h;
        double iw = Math.Max(w - 2 * Frame, 0), ih = Math.Max(h - 2 * Frame, 0);
        canvas.Clip = Round ? new EllipseGeometry(new Rect(0, 0, iw, ih)) : new RectangleGeometry(new Rect(0, 0, iw, ih), 8, 8);
    }

    /// <summary>See-through: the whole map, frame and label fade so the game shows behind
    /// them (the window itself is transparent). Hover controls stay fully visible.</summary>
    void Fade(double value)
    {
        canvas.Opacity = value;
        frame.Opacity = value;
        label.Opacity = Math.Max(value, 0.5);
    }

    void ShowControls(bool show)
    {
        controls.IsVisible = show;
        opacityPanel.IsVisible = show;
        grip.IsVisible = show;
    }

    void Update() => guidance.Text = hub.Error ?? hub.Guidance();

    static Button Small(string label, string tip, Action action)
    {
        var button = new Button
        {
            Content = label,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = Brush.Parse("#E60B1118"),
            BorderBrush = Ui.Gold,
            BorderThickness = new Thickness(1),
            Foreground = Brushes.White,
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>The dark frame with gold trim and N/E/S/W marks: a ring, or a rounded rectangle.</summary>
    sealed class FrameControl : Control, Avalonia.Rendering.ICustomHitTest
    {
        static readonly IBrush Dark = Brush.Parse("#F00B1118");
        static readonly IPen Gold = new Pen(Brush.Parse("#C9A227"), 1.5);
        static readonly IPen Shadow = new Pen(Brush.Parse("#80000000"), 3);
        static readonly Typeface Face = new("Noto Sans", FontStyle.Normal, FontWeight.Bold);
        public bool Round { get; set; } = true;

        /// <summary>Only the frame band takes the pointer; the map inside keeps panning and clicks.</summary>
        public bool HitTest(Point p)
        {
            if (Round)
            {
                var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
                double r = Math.Min(Bounds.Width, Bounds.Height) / 2, d = Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
                return d >= r - Frame && d <= r;
            }

            var outer = new Rect(Bounds.Size);
            return outer.Contains(p) && !outer.Deflate(Frame).Contains(p);
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 2 * Frame || Bounds.Height <= 2 * Frame)
                return;
            if (Round)
                RenderRing(context);
            else
                RenderRectangle(context);
        }

        void RenderRing(DrawingContext context)
        {
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double r = Math.Min(Bounds.Width, Bounds.Height) / 2;
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.SetFillRule(FillRule.EvenOdd);
                Circle(g, c, r - 1);
                Circle(g, c, r - Frame);
            }

            context.DrawGeometry(Dark, null, band);
            context.DrawEllipse(null, Shadow, c, r - Frame + 1, r - Frame + 1);
            context.DrawEllipse(null, Gold, c, r - 1.5, r - 1.5);
            context.DrawEllipse(null, Gold, c, r - Frame, r - Frame);
            for (int deg = 30; deg < 360; deg += 30)
            {
                if (deg % 90 == 0)
                    continue;
                double a = deg * Math.PI / 180;
                context.DrawLine(Gold, new Point(c.X + Math.Sin(a) * (r - Frame + 3), c.Y - Math.Cos(a) * (r - Frame + 3)), new Point(c.X + Math.Sin(a) * (r - 4), c.Y - Math.Cos(a) * (r - 4)));
            }

            double mid = r - Frame / 2;
            Letter(context, "N", new Point(c.X, c.Y - mid));
            Letter(context, "E", new Point(c.X + mid, c.Y));
            Letter(context, "S", new Point(c.X, c.Y + mid));
            Letter(context, "W", new Point(c.X - mid, c.Y));
        }

        void RenderRectangle(DrawingContext context)
        {
            var outer = new Rect(Bounds.Size).Deflate(1);
            var inner = new Rect(Bounds.Size).Deflate(Frame);
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.SetFillRule(FillRule.EvenOdd);
                Rounded(g, outer, 12);
                Rounded(g, inner, 8);
            }

            context.DrawGeometry(Dark, null, band);
            context.DrawRectangle(null, Shadow, inner.Inflate(1), 8, 8);
            context.DrawRectangle(null, Gold, outer.Deflate(0.5), 12, 12);
            context.DrawRectangle(null, Gold, inner, 8, 8);
            double half = Frame / 2;
            Letter(context, "N", new Point(Bounds.Width / 2, half));
            Letter(context, "E", new Point(Bounds.Width - half, Bounds.Height / 2));
            Letter(context, "S", new Point(Bounds.Width / 2, Bounds.Height - half));
            Letter(context, "W", new Point(half, Bounds.Height / 2));
        }

        static void Letter(DrawingContext context, string text, Point at)
        {
            var format = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, text == "N" ? Brush.Parse("#FFD54F") : Brushes.Gainsboro);
            context.DrawText(format, new Point(at.X - format.Width / 2, at.Y - format.Height / 2));
        }

        static void Circle(StreamGeometryContext g, Point c, double r)
        {
            g.BeginFigure(new Point(c.X + r, c.Y), true);
            g.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
            g.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }

        static void Rounded(StreamGeometryContext g, Rect r, double radius)
        {
            g.BeginFigure(new Point(r.Left + radius, r.Top), true);
            g.LineTo(new Point(r.Right - radius, r.Top));
            g.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(r.Right, r.Bottom - radius));
            g.ArcTo(new Point(r.Right - radius, r.Bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(r.Left + radius, r.Bottom));
            g.ArcTo(new Point(r.Left, r.Bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(r.Left, r.Top + radius));
            g.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
    }
}
