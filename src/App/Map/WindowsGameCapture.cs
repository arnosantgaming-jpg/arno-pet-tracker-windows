using System.Runtime.InteropServices;
using System.Text;

namespace ArnoA2.App.Map;

/// <summary>
/// Windows: copies a rectangle of the game window's client area from the screen with GDI
/// (BitBlt), about a millisecond for the minimap corner. It copies what is on screen, so it
/// works in borderless or windowed mode while the game is visible; exclusive fullscreen can
/// come out black, and anything drawn over the game's minimap (such as this tracker's own
/// mini-map) is copied too. Nothing is read from or injected into the game process.
/// </summary>
public sealed class WindowsGameCapture : IGameCapture
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
    const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    IntPtr window;

    public bool Available => OperatingSystem.IsWindows();
    public (int Width, int Height)? Size { get; private set; }

    public bool Find()
    {
        window = IntPtr.Zero;
        Size = null;
        var title = new StringBuilder(256);
        EnumWindows((candidate, _) =>
        {
            if (!IsWindowVisible(candidate))
                return true;
            title.Clear();
            GetWindowText(candidate, title, title.Capacity);
            if (!title.ToString().StartsWith("AION2", StringComparison.Ordinal))
                return true;
            window = candidate;
            return false;
        }, IntPtr.Zero);
        if (window == IntPtr.Zero || !GetClientRect(window, out var client))
            return false;
        Size = (client.Right - client.Left, client.Bottom - client.Top);
        return Size.Value.Width > 0 && Size.Value.Height > 0;
    }

    public byte[]? Read(int x, int y, int width, int height)
    {
        if ((window == IntPtr.Zero || !IsWindow(window)) && !Find())
            return null;
        var origin = new Point { X = x, Y = y };
        if (!ClientToScreen(window, ref origin))
            return null;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, SrcCopy | CaptureBlt))
                return null;
            SelectObject(memory, previous);
            // Top-down 32-bit BGRX rows (negative height).
            var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var bgrx = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, bgrx, ref info, 0) != height)
                return null;
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < bgrx.Length; i += 4, j += 3)
            {
                rgb[j] = bgrx[i + 2];
                rgb[j + 1] = bgrx[i + 1];
                rgb[j + 2] = bgrx[i];
            }

            return rgb;
        }
        finally
        {
            SelectObject(memory, previous); // a selected bitmap cannot be deleted
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
    }
}
