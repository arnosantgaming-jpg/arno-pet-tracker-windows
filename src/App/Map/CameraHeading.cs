namespace ArnoA2.App.Map;

/// <summary>
/// Which way your camera looks, read from the bright view cone on the game's own minimap
/// (top-right corner): about thirty grabs a second of that small area, nothing else. The
/// cone's two sides are straight lines out from the centre, which terrain rarely is, so
/// the reading looks for a brightening edge and a darkening edge one cone width apart.
/// Uses the same minimap area as <see cref="MinimapTracker"/>, but its own capture: that one
/// grabs four times a second and only while placed, the cone needs thirty for smooth turns.
/// On Windows the corner is copied from the screen, so it needs the game visible (borderless
/// or windowed) and our own mini-map not placed over the game's minimap.
/// </summary>
public sealed class CameraHeading : IDisposable
{
    // Measured: the cone is 116° from edge to edge; sides are read between these radii.
    const int HalfCone = 58, Inner = 24, Outer = 70, Spread = 2;
    readonly CancellationTokenSource stop = new();
    readonly Thread thread;

    public CameraHeading()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Camera heading", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Degrees clockwise from north, or null while the cone cannot be read
    /// (game not found, minimap hidden, world map open).</summary>
    public double? Degrees { get => degrees as double?; private set => degrees = value; }
    // Boxed so the UI thread never sees half of a value being written.
    volatile object? degrees;
    public string? Status { get; private set; } = "Looking for the game window…";

    void Run()
    {
        using var capture = GameCapture.Create();
        if (!capture.Available)
        {
            Status = OperatingSystem.IsWindows() ? "Camera reading is unavailable." : "Turning with the camera needs an X11/XWayland display.";
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                Step(capture);
            }
            catch (Exception ex)
            {
                Status = "Camera reading: " + ex.Message;
                Degrees = null;
            }

            stop.Token.WaitHandle.WaitOne(33);
        }
    }

    void Step(IGameCapture capture)
    {
        if (capture.Size == null && !capture.Find())
        {
            (Degrees, Status) = (null, "Game window not found.");
            return;
        }

        var (width, height) = capture.Size!.Value;
        var (x, y, w, h) = MinimapTracker.Area(width, height);
        double scale = height / 1080.0;
        var rgb = capture.Read(x, y, w, h);
        if (rgb == null)
        {
            capture.Find();
            (Degrees, Status) = (null, "Game window not readable.");
            return;
        }

        // Raw readings; the mini-map eases toward them every frame.
        Degrees = Read(rgb, w, h, scale);
        Status = Degrees == null ? "Minimap cone not found (is the minimap shown?)." : null;
    }

    /// <summary>An angle difference brought into −180…180.</summary>
    public static double Wrap(double d) => ((d + 180) % 360 + 360) % 360 - 180;

    /// <summary>One frame of easing from the shown heading toward the latest reading (the
    /// short way round), or null while there is no reading.</summary>
    public static double? Ease(double? shown, double? target, double seconds)
    {
        if (target is not double t)
            return null;
        double next = shown is double s ? s + Wrap(t - s) * (1 - Math.Exp(-seconds * 18)) : t;
        return (next % 360 + 360) % 360;
    }

    /// <summary>The cone's direction in an RGB image of the minimap area, or null.</summary>
    public static double? Read(byte[] rgb, int w, int h, double scale = 1)
    {
        double cx = w / 2.0, cy = h / 2.0;
        int inner = (int)(Inner * scale), outer = (int)(Outer * scale);
        var pos = new double[360];
        var neg = new double[360];
        var count = new int[360];
        for (int r = inner; r < outer; r++)
        {
            // Darkest channel: the cone's white haze lifts it most; sand and water stay low.
            var ring = new int[360];
            for (int t = 0; t < 360; t++)
            {
                double a = t * Math.PI / 180;
                int px = (int)(cx + r * Math.Sin(a)), py = (int)(cy - r * Math.Cos(a));
                if (px < 0 || py < 0 || px >= w || py >= h)
                {
                    ring[t] = -1;
                    continue;
                }

                int i = (py * w + px) * 3;
                ring[t] = Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]));
            }

            for (int t = 0; t < 360; t++)
            {
                int ahead = ring[(t + Spread) % 360], behind = ring[(t - Spread + 360) % 360];
                if (ring[t] < 0 || ahead < 0 || behind < 0)
                    continue;
                int d = ahead - behind;
                pos[t] += Math.Clamp(d, 0, 60);
                neg[t] += Math.Clamp(-d, 0, 60);
                count[t]++;
            }
        }

        var score = new double[360];
        for (int t = 0; t < 360; t++)
        {
            int rise = (t - HalfCone + 360) % 360, fall = (t + HalfCone) % 360;
            score[t] = pos[rise] / Math.Max(count[rise], 1) + neg[fall] / Math.Max(count[fall], 1);
        }

        int best = 0;
        for (int t = 1; t < 360; t++)
            if (score[t] > score[best])
                best = t;
        double second = 0;
        for (int t = 0; t < 360; t++)
            if (Math.Abs(Wrap(t - best)) > 30)
                second = Math.Max(second, score[t]);
        // Measured on 237 frames: a real cone scores 59+ and at least 1.2× anything else.
        if (score[best] < 45 || score[best] < 1.15 * second)
            return null;
        return best;
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(500);
        stop.Dispose();
    }
}
