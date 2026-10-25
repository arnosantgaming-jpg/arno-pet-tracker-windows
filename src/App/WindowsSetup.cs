using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArnoA2.App;

/// <summary>Windows-only conveniences: is Npcap installed, and start with Windows.</summary>
public static class WindowsSetup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "ArnoPetTracker";

    public static bool NpcapInstalled =>
        !OperatingSystem.IsWindows() ||
        File.Exists(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll")) ||
        File.Exists(Path.Combine(Environment.SystemDirectory, "wpcap.dll"));

    public static void OpenNpcapDownload() => Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true })?.Dispose();

    /// <summary>Per-user "start with Windows" (HKCU\…\Run), pointing at this executable.</summary>
    public static bool StartsWithWindows
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            if (!OperatingSystem.IsWindows())
                return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

/// <summary>Shown at start on Windows when Npcap is missing: what it is, why, and one button.</summary>
public sealed class NpcapWindow : Window
{
    public NpcapWindow()
    {
        Title = "Arno's Pet Tracker · one more step";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        var status = Ui.Note("");
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "One more step: install Npcap", FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "The tracker reads your pet progress from the game's network messages. Windows needs a free helper called Npcap for that. It is made by the Nmap project and is used by many network tools.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "1. Click \"Download Npcap\" and run the installer.\n2. Keep the default options. Leave \"Restrict Npcap driver's access to Administrators only\" unticked.\n3. Come back here and click \"Check again\".",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var download = Ui.Button("Download Npcap", WindowsSetup.OpenNpcapDownload);
        download.Background = Ui.Gold;
        download.Foreground = Brushes.Black;
        buttons.Children.Add(download);
        buttons.Children.Add(Ui.Button("Check again", () =>
        {
            if (WindowsSetup.NpcapInstalled)
                Close();
            else
                status.Text = "Npcap was not found yet. If you just installed it, wait a moment and try again.";
        }));
        buttons.Children.Add(Ui.Button("Later", Close, "The tracker opens without live data until Npcap is installed"));
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        Content = panel;
    }
}
