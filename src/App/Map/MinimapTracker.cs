using Avalonia.Threading;

namespace ArnoA2.App.Map;

/// <summary>
/// Reads the in-game minimap about four times a second and places it on the region tiles.
/// The network position seeds the search (exact fixes tightly, estimates loosely); a
/// confident match is precise to about 2 m and updates while the player only walks, which
/// the network does not report. Runs on its own thread; results are posted to the hub.
/// </summary>
public sealed class MinimapTracker : IDisposable
{
    // Minimap terrain area at 1920×1080, anchored to the top-right corner.
    const int RightMargin = 19, Top = 87, AreaWidth = 219, AreaHeight = 147;
    readonly MapHub hub;
    readonly TerrainCache terrain = new(Path.Combine(AppContext.BaseDirectory, "maps"));
    readonly CancellationTokenSource stop = new();
    readonly Thread thread;
    volatile Seed? seed;
    double? ratio;
    MinimapFix? last;
    DateTime lastAt;
    int misses;

    public sealed record Seed(string Region, double X, double Y, bool Exact);

    public MinimapTracker(MapHub hub)
    {
        this.hub = hub;
        ratio = hub.Preferences.MinimapRatio;
        thread = new Thread(Run) { IsBackground = true, Name = "Minimap reader", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Latest network position, set from the UI thread.</summary>
    public void Update(Seed? next) => seed = next;

    void Run()
    {
        using var capture = new GameCapture();
        if (!capture.Available)
        {
            Post(null, "Minimap reading needs an X11/XWayland display");
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                Step(capture);
            }
            catch (Exception ex)
            {
                Post(null, "Minimap reader: " + ex.Message);
            }

            int wait = 250 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            stop.Token.WaitHandle.WaitOne(Math.Max(50, wait));
        }
    }

    void Step(GameCapture capture)
    {
        if (seed is not { } s || !WorldMaps.All.TryGetValue(s.Region, out var map))
            return;
        if (capture.Size == null && !capture.Find())
        {
            Post(null, "Game window not found");
            return;
        }

        var (width, height) = capture.Size!.Value;
        double scale = height / 1080.0;
        int w = (int)Math.Round(AreaWidth * scale), h = (int)Math.Round(AreaHeight * scale);
        int x = width - (int)Math.Round((RightMargin + AreaWidth) * scale), y = (int)Math.Round(Top * scale);
        var rgb = capture.Read(x, y, w, h);
        if (rgb == null)
        {
            capture.Find();
            Post(null, "Game window not readable");
            return;
        }

        bool tracking = last != null && (DateTime.UtcNow - lastAt).TotalSeconds < 2;
        if (ratio == null && !s.Exact)
            // Measured default (1080p, default minimap zoom); the minimap scales with the UI.
            ratio = MinimapMatcher.DefaultRatio * 1080 / height;
        if (ratio == null)
        {
            // Calibrate the minimap scale near an exact network position (after repeated misses).

            var calibrated = MinimapMatcher.Calibrate(terrain, map, rgb, w, h, s.X, s.Y, 60);
            if (calibrated == null)
            {
                Post(null, "Minimap not recognised (is it visible?)");
                return;
            }

            ratio = calibrated.Ratio;
            hub.Preferences.MinimapRatio = ratio;
            Dispatcher.UIThread.Post(hub.Save);
        }

        (double X, double Y) from = tracking ? (last!.X, last.Y) : (s.X, s.Y);
        double radius = tracking ? 25 : s.Exact ? 40 : 120;
        var fix = MinimapMatcher.Locate(terrain, map, rgb, w, h, from.X, from.Y, radius, ratio.Value);
        // An exact network fix is ground truth: a minimap match far from it is a false match.
        if (fix != null && s.Exact && Math.Sqrt(Math.Pow(fix.X - s.X, 2) + Math.Pow(fix.Y - s.Y, 2)) * map.Metres > 30)
            fix = null;
        if (fix == null)
        {
            last = null;
            // Repeated misses near exact fixes usually mean the minimap zoom changed.
            if (s.Exact && ++misses >= 12)
            {
                ratio = null;
                misses = 0;
            }

            Post(null, "Minimap not matched");
            return;
        }

        misses = 0;
        last = fix;
        lastAt = DateTime.UtcNow;
        Post(fix with { }, null, s.Region);
    }

    void Post(MinimapFix? fix, string? status, string? region = null) => Dispatcher.UIThread.Post(() => hub.AcceptMinimap(fix, status, region));

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(1000);
        stop.Dispose();
    }
}
