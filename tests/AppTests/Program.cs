using ArnoA2.App;
using ArnoA2.Protocol;
using ArnoA2.Tracker;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Renders every window with a synthetic session and checks the basics. Usage: AppTests <output folder>
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "test-results/app");
Directory.CreateDirectory(output);
string data = Path.Combine(output, "data");
if (Directory.Exists(data))
    Directory.Delete(data, true);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
int checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok)
        throw new Exception("FAILED: " + what);
}

string Text(Control c) => string.Join("\n", c.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
void Save(Window w, string name)
{
    for (int i = 0; i < 3; i++)
        Dispatcher.UIThread.RunJobs();
    using var frame = w.CaptureRenderedFrame() ?? throw new Exception("no frame");
    frame.Save(Path.Combine(output, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
}

var game = GameData.Instance;
int dratona = game.Pets.Values.Single(p => p.Name == "Dratona").Id;
int spirit = game.Pets.Values.Single(p => p.Name == "Ancient Spirit").Id;
var store = new Store(data);
var state = new AppState(store, daemonPath: null);
var now = DateTimeOffset.UtcNow;
// A login: collection, character, a few rewards, a position in Verteron near Eastern Cantas Valley.
var engine = state.Engine;
var collection = new CollectionSnapshot(0, new Dictionary<int, int> { [dratona] = 2, [spirit] = 1 }, new Dictionary<int, int> { [dratona] = 40, [spirit] = 22 });
engine.Collection.Apply(collection, now);
foreach (var (pet, souls) in new[] { (dratona, 3), (spirit, 2) })
{
    engine.Collection.Gain(pet, souls);
    engine.Recent.Touch(pet, "", now, true);
    engine.Session.Souls(pet, souls, now);
}

state.Positions.Observe(new ZoneEntered(1010, -72190f, -69614f), null, now);
var maps = new MapHub(state, data);
var tracker = new TrackerWindow(state, maps);
tracker.Show();
Save(tracker, "tracker.png");
Check(Text(tracker).Contains("Dratona") && Text(tracker).Contains("43/75") && Text(tracker).Contains("Session: 5 souls"), "tracker lists recent pets with souls and the session");

var finder = new FinderWindow(state, maps);
finder.Show();
finder.GetVisualDescendants().OfType<TextBox>().First().Text = "Dratona";
Dispatcher.UIThread.RunJobs();
Save(finder, "finder.png");
Check(Text(finder).Contains("Souls from:") && Text(finder).Contains("3 pets"), "finder searches and shows sources");

var settings = new SettingsWindow(state, maps, tracker);
settings.Show();
Save(settings, "settings.png");
Check(Text(settings).Contains("Mini-map") && Text(settings).Contains("Capture"), "settings sections");

maps.Preferences.MiniShape = "Round";
tracker.OpenMiniMap();
var mini = tracker.MiniMap!;
mini.StopCamera(); // headings below are set by hand
Thread.Sleep(400);
foreach (var b in mini.GetVisualDescendants().OfType<Border>().Where(b => b.Child is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal } sp && sp.Children.OfType<GoldSlider>().Any()))
    b.IsVisible = true; // show the hover controls for the screenshot
Save(mini, "minimap-round.png");
var slider = mini.GetVisualDescendants().OfType<GoldSlider>().Single();
var pill = slider.GetVisualAncestors().OfType<Border>().First();
var sliderBox = slider.TransformToVisual(pill)!.Value.Transform(new Point(0, 0));
double label = mini.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Opacity").TransformToVisual(pill)!.Value.Transform(new Point(0, 0)).Y + mini.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Opacity").Bounds.Height / 2;
double sliderMiddle = sliderBox.Y + slider.Bounds.Height / 2;
Check(sliderBox.Y >= 0 && sliderBox.Y + slider.Bounds.Height <= pill.Bounds.Height + 0.5, $"opacity slider fits inside its pill ({sliderBox.Y:F1}+{slider.Bounds.Height:F1} in {pill.Bounds.Height:F1})");
Check(Math.Abs(sliderMiddle - pill.Bounds.Height / 2) <= 1 && Math.Abs(label - sliderMiddle) <= 1.5, $"slider and label centred in the pill (label {label:F1}, slider {sliderMiddle:F1}, pill middle {pill.Bounds.Height / 2:F1})");
slider.Value = 0.4;
Check(Math.Abs(mini.GetVisualDescendants().OfType<MapCanvas>().Single().Opacity - 0.4) < 0.01 && Math.Abs(slider.GetVisualAncestors().OfType<Border>().First().Opacity - 1) < 0.01, "opacity fades the whole map, not the controls");
Save(mini, "minimap-faded.png");
slider.Value = 0.8;
tracker.SetMiniMapShape(false);
mini.Width = 380;
mini.Height = 260;
Thread.Sleep(200);
Save(mini, "minimap-rectangle.png");
Check(maps.Player != null && maps.Region == "verteron", "position placed on Verteron");

// A long guidance line in a narrow mini-map scrolls instead of being cut off.
mini.Width = 200;
mini.Height = 230;
var marquee = mini.GetVisualDescendants().OfType<MarqueeText>().Single();
marquee.Text = "1038 m NE · Krall Warshaman, Dracuni Herbalist, Forest Spirit";
for (int i = 0; i < 5; i++)
    Dispatcher.UIThread.RunJobs();
Check(marquee.Scrolls && marquee.Bounds.Width <= mini.Width, "long guidance fits the window and scrolls");
marquee.Advance(1.0);
Check(marquee.Offset == 0, "guidance holds still while pausing on its start");
for (int i = 0; i < 30; i++)
    marquee.Advance(0.05);
Check(marquee.Offset > 0, "guidance scrolls after its pause");
double scrolled = marquee.Offset;
marquee.Text = "1037 m NE · Krall Warshaman, Dracuni Herbalist, Forest Spirit";
marquee.Advance(0.05);
Check(marquee.Offset > scrolled, "guidance keeps scrolling when its distance changes");
Save(mini, "minimap-scrolling.png");

// Turning with the camera: the player becomes an arrow pointing the camera way, and the
// map can turn about the player so that way points up.
var canvas = mini.Canvas;
slider.Value = 1;
tracker.SetMiniMapShape(true);
mini.Width = 300;
mini.Height = 330;
for (int i = 0; i < 5; i++)
    Dispatcher.UIThread.RunJobs();
Thread.Sleep(100);
var middle = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
bool Blue(Point at)
{
    for (int i = 0; i < 3; i++)
        Dispatcher.UIThread.RunJobs();
    using var frame = mini.CaptureRenderedFrame() ?? throw new Exception("no frame");
    var p = canvas.TranslatePoint(at, mini)!.Value;
    var pixel = new byte[4];
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        frame.CopyPixels(new PixelRect((int)p.X, (int)p.Y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
    }
    finally
    {
        handle.Free();
    }

    // The arrow's fill is #4FC3F7: strong blue and green, little red (lighter where it is smoothed).
    bool rgba = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888;
    int r = pixel[rgba ? 0 : 2], g = pixel[1], b = pixel[rgba ? 2 : 0];
    return b > 220 && g > 170 && r < 150;
}

canvas.Follow = true;
mini.ShowHeading(90);
Thread.Sleep(100);
Check(canvas.Turned == 0 && Blue(middle + new Vector(7, 0)) && !Blue(middle + new Vector(0, -7)) && !Blue(middle + new Vector(-7, 0)), "north up, looking east: the arrow points right");
Check(Blue(middle), "the arrow covers the middle (the dot has a white centre)");
Save(mini, "minimap-arrow.png");
var player = maps.Player!.Value;
var east = canvas.Screen(player.X + 0.002, player.Y);
Check(east.X > middle.X + 1 && Math.Abs(east.Y - middle.Y) < 0.5, "north up: east is to the right");

canvas.TurnMap = true;
mini.ShowHeading(37);
Check(Math.Abs(canvas.Turned - 37) < 1e-9 && Blue(middle + new Vector(0, -7)) && !Blue(middle + new Vector(7, 0)), "turned with the camera: the arrow points up");
double rad = 37 * Math.PI / 180;
var ahead = canvas.Screen(player.X + 0.002 * Math.Sin(rad), player.Y - 0.002 * Math.Cos(rad));
Check(Math.Abs(ahead.X - middle.X) < 0.5 && ahead.Y < middle.Y - 1, $"what the camera looks at shows straight above the player ({ahead})");
var north = canvas.Screen(player.X, player.Y - 0.002);
Check(north.X < middle.X - 1 && north.Y < middle.Y, "north moves left of up when looking north-east");
foreach (var (x, y) in new[] { (0.1, 0.2), (0.5, 0.5), (0.93, 0.07), (player.X, player.Y) })
{
    var (rx, ry) = canvas.Region(canvas.Screen(x, y));
    Check(Math.Abs(rx - x) < 1e-9 && Math.Abs(ry - y) < 1e-9, $"turned Screen/Region round trip at {x}, {y}");
}

mini.ShowHeading(251);
var spot = canvas.Screen(0.3, 0.6);
Check(Math.Abs(canvas.Region(spot).X - 0.3) < 1e-9 && Math.Abs(canvas.Region(spot).Y - 0.6) < 1e-9, "a click on the turned map lands on the place under it");
Save(mini, "minimap-turned.png");
tracker.SetMiniMapShape(false);
mini.Width = 380;
mini.Height = 290;
Save(mini, "minimap-turned-rectangle.png");
tracker.SetMiniMapShape(true);
mini.Width = 300;
mini.Height = 330;
for (int i = 0; i < 5; i++)
    Dispatcher.UIThread.RunJobs();

// Panning and zoom follow the pointer on the turned map.
// Grab a place away from the numbered spots (a click on one makes it the target instead).
var grab = new[] { new Vector(-40, -30), new Vector(40, -30), new Vector(-40, 30), new Vector(40, 30), new Vector(0, -60), new Vector(-60, 0) }
    .Select(v => middle + v)
    .MaxBy(p => maps.Hotspots.Select(h => canvas.Screen(h.X, h.Y)).Select(h => Math.Sqrt((h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y))).DefaultIfEmpty(999).Min());
var under = canvas.Region(grab);
var drop = grab + new Vector(25, 35);
var target = maps.TargetSpot;
mini.MouseDown(canvas.TranslatePoint(grab, mini)!.Value, Avalonia.Input.MouseButton.Left);
mini.MouseMove(canvas.TranslatePoint(drop, mini)!.Value);
mini.MouseUp(canvas.TranslatePoint(drop, mini)!.Value, Avalonia.Input.MouseButton.Left);
var moved = canvas.Region(drop);
Check(maps.TargetSpot == target && !canvas.Follow && Math.Abs(moved.X - under.X) < 1e-6 && Math.Abs(moved.Y - under.Y) < 1e-6, "dragging the turned map keeps the grabbed place under the pointer");
var anchor = middle + new Vector(30, -20);
var before = canvas.Region(anchor);
canvas.ZoomAt(anchor, 1.25);
var after = canvas.Region(anchor);
Check(Math.Abs(after.X - before.X) < 1e-9 && Math.Abs(after.Y - before.Y) < 1e-9, "zooming the turned map keeps the place under the pointer");
canvas.ZoomAt(anchor, 0.8);
canvas.Follow = true;

mini.ShowHeading(null);
Check(canvas.Turned == 0 && Blue(middle + new Vector(0, -6)) && Blue(middle + new Vector(0, 6)) && !Blue(middle), "camera not read: north up with the plain dot");
canvas.TurnMap = false;

// Easing goes the short way round and settles on the reading.
double? shown = 350;
shown = ArnoA2.App.Map.CameraHeading.Ease(shown, 10, 1 / 60.0);
Check(shown is double first && (first > 350 || first < 10), $"easing from 350° to 10° crosses north ({shown})");
for (int i = 0; i < 60; i++)
    shown = ArnoA2.App.Map.CameraHeading.Ease(shown, 10, 1 / 60.0);
Check(shown is double settled && Math.Abs(settled - 10) < 0.1, "easing settles on the reading within a second");
Check(ArnoA2.App.Map.CameraHeading.Ease(10, null, 1 / 60.0) == null, "no reading: no heading");

// The camera cone on a made-up minimap: bright haze 116° wide over speckled sand.
var random = new Random(7);
foreach (double facing in new[] { 0.0, 37, 130, 251, 333 })
{
    var sand = new byte[219 * 147 * 3];
    for (int py = 0; py < 147; py++)
        for (int px = 0; px < 219; px++)
        {
            int i = (py * 219 + px) * 3, n = random.Next(40);
            (sand[i], sand[i + 1], sand[i + 2]) = ((byte)(150 + n), (byte)(120 + n), (byte)(60 + n / 2));
            double angle = (Math.Atan2(px - 109.5, 73.5 - py) * 180 / Math.PI + 360) % 360, off = Math.Abs(((angle - facing) % 360 + 540) % 360 - 180);
            if (off <= 58 && Math.Sqrt((px - 109.5) * (px - 109.5) + (py - 73.5) * (py - 73.5)) > 10)
                for (int c = 0; c < 3; c++)
                    sand[i + c] = (byte)(sand[i + c] + (255 - sand[i + c]) * 0.45);
        }

    double? read = ArnoA2.App.Map.CameraHeading.Read(sand, 219, 147);
    Check(read is double r && Math.Abs(((r - facing) % 360 + 540) % 360 - 180) <= 3, $"the camera cone at {facing}° reads as {read}");
}

var plain = new byte[219 * 147 * 3];
for (int i = 0; i < plain.Length; i++)
    plain[i] = (byte)(100 + random.Next(30));
Check(ArnoA2.App.Map.CameraHeading.Read(plain, 219, 147) == null, "no cone (minimap hidden): no reading, the map stays north up");
tracker.OpenFullMap();
var full = tracker.FullMap!;
Thread.Sleep(400);
Save(full, "fullmap.png");
Check(Text(full).Contains("Best spots"), "full map lists spots");
var npcap = new NpcapWindow();
npcap.Show();
Save(npcap, "npcap.png");
Check(Text(npcap).Contains("Download Npcap") || npcap.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Download Npcap")), "Npcap help window offers the download");
Console.WriteLine($"{checks} app checks passed; screenshots in {output}");

