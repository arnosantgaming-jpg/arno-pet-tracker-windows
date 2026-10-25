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

