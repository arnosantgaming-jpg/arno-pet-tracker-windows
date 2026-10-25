using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace ArnoA2.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Lets the Windows installer run its install/uninstall steps; returns at once otherwise.
        if (OperatingSystem.IsWindows())
            Velopack.VelopackApp.Build().Run();
        // One tracker per user: a second start just exits.
        string lockPath = Path.Combine(Path.GetDirectoryName(ArnoA2.Protocol.CaptureChannel.DefaultPath())!, "app.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream instance;
        try
        {
            instance = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Arno's Pet Tracker is already running.");
            return 0;
        }

        using (instance)
            return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public static TrackerWindow? Tracker { get; private set; }
    AppState? state;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = AppState.OpenStore();
            string daemon = Path.Combine(AppContext.BaseDirectory, "capture", OperatingSystem.IsWindows() ? "arnoa2-capture.exe" : "arnoa2-capture");
            state = new AppState(store, File.Exists(daemon) ? daemon : null);
            var maps = new MapHub(state, store.Folder);
            Tracker = new TrackerWindow(state, maps);
            desktop.MainWindow = Tracker;
            desktop.ShutdownRequested += (_, _) => state.Stop();
            Tracker.Closed += (_, _) => state.Stop();
            state.Start();
            if (OperatingSystem.IsWindows() && !WindowsSetup.NpcapInstalled)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => new NpcapWindow().Show(Tracker));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
