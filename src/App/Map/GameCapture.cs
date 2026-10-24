using System.Runtime.InteropServices;

namespace ArnoA2.App.Map;

/// <summary>
/// Reads a small rectangle of the AION 2 window through X11 (the game runs under XWayland
/// with Proton). Only the minimap corner is copied, about 0.4 ms per grab at 1080p. It reads
/// pixels the game has already drawn; nothing is injected into or read from the game process.
/// Not thread-safe: use one instance from one thread.
/// </summary>
public sealed class GameCapture : IDisposable
{
    const string Lib = "libX11.so.6";
    [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Lib)] static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, bool delete, IntPtr type, out IntPtr actualType, out int format, out ulong items, out ulong after, out IntPtr data);
    [DllImport(Lib)] static extern int XFree(IntPtr data);
    [DllImport(Lib)] static extern int XGetGeometry(IntPtr display, IntPtr window, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport(Lib)] static extern IntPtr XGetImage(IntPtr display, IntPtr window, int x, int y, uint width, uint height, ulong planes, int format);
    [DllImport(Lib)] static extern IntPtr XSetErrorHandler(IntPtr handler);
    delegate int ErrorHandler(IntPtr display, IntPtr error);
    delegate int DestroyImage(IntPtr image);
    // A window that closes between lookup and read raises BadWindow/BadMatch, and Xlib's
    // default handler exits the process. The handler is process-wide and Avalonia installs
    // its own, so errors on our display are ignored and all others go to the previous one.
    static readonly ErrorHandler Chained = OnError;
    static IntPtr previous;
    static readonly HashSet<IntPtr> ours = new();
    static bool handlerSet;

    static int OnError(IntPtr source, IntPtr error)
    {
        lock (ours)
            if (ours.Contains(source))
                return 0;
        return previous != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<ErrorHandler>(previous)(source, error) : 0;
    }

    IntPtr display, window;
    public GameCapture()
    {
        display = XOpenDisplay(IntPtr.Zero);
        lock (ours)
        {
            ours.Add(display);
            if (!handlerSet)
            {
                previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(Chained));
                handlerSet = true;
            }
        }
    }

    public bool Available => display != IntPtr.Zero;
    public (int Width, int Height)? Size { get; private set; }

    /// <summary>Finds the game window by its title (refreshed when it is lost).</summary>
    public bool Find()
    {
        window = IntPtr.Zero;
        Size = null;
        if (display == IntPtr.Zero)
            return false;
        var root = XDefaultRootWindow(display);
        var list = XInternAtom(display, "_NET_CLIENT_LIST", false);
        var name = XInternAtom(display, "_NET_WM_NAME", false);
        var utf8 = XInternAtom(display, "UTF8_STRING", false);
        if (XGetWindowProperty(display, root, list, 0, 4096, false, IntPtr.Zero, out _, out _, out ulong count, out _, out var data) != 0 || data == IntPtr.Zero)
            return false;
        try
        {
            for (int i = 0; i < (int)count; i++)
            {
                var candidate = Marshal.ReadIntPtr(data, i * IntPtr.Size);
                if (XGetWindowProperty(display, candidate, name, 0, 256, false, utf8, out _, out _, out ulong length, out _, out var text) != 0 || text == IntPtr.Zero)
                    continue;
                string title = Marshal.PtrToStringUTF8(text, (int)length);
                XFree(text);
                if (!title.StartsWith("AION2", StringComparison.Ordinal))
                    continue;
                window = candidate;
                if (XGetGeometry(display, window, out _, out _, out _, out uint width, out uint height, out _, out _) != 0)
                    Size = ((int)width, (int)height);
                return Size != null;
            }
        }
        finally
        {
            XFree(data);
        }

        return false;
    }

    /// <summary>RGB bytes of the rectangle, or null when the window is gone.</summary>
    public byte[]? Read(int x, int y, int width, int height)
    {
        if (window == IntPtr.Zero && !Find())
            return null;
        var image = XGetImage(display, window, x, y, (uint)width, (uint)height, ulong.MaxValue, 2);
        if (image == IntPtr.Zero)
        {
            window = IntPtr.Zero;
            return null;
        }

        try
        {
            // XImage: data at 16, bytes_per_line at 44, bits_per_pixel at 48 (LP64).
            var pixels = Marshal.ReadIntPtr(image, 16);
            int stride = Marshal.ReadInt32(image, 44), bits = Marshal.ReadInt32(image, 48);
            if (bits != 32)
                return null;
            var row = new byte[stride];
            var rgb = new byte[width * height * 3];
            for (int j = 0; j < height; j++)
            {
                Marshal.Copy(pixels + j * stride, row, 0, stride);
                for (int i = 0; i < width; i++)
                {
                    // ZPixmap 24/32-bit on little-endian X servers is BGRX.
                    rgb[(j * width + i) * 3] = row[i * 4 + 2];
                    rgb[(j * width + i) * 3 + 1] = row[i * 4 + 1];
                    rgb[(j * width + i) * 3 + 2] = row[i * 4];
                }
            }

            return rgb;
        }
        finally
        {
            Marshal.GetDelegateForFunctionPointer<DestroyImage>(Marshal.ReadIntPtr(image, 96))(image);
        }
    }

    public void Dispose()
    {
        if (display != IntPtr.Zero)
            XCloseDisplay(display);
        lock (ours)
            ours.Remove(display);
        display = IntPtr.Zero;
    }
}
