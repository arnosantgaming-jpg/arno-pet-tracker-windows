using System.Runtime.InteropServices;

namespace ArnoA2.Capture;

/// <summary>
/// After the capture handle is open it keeps working without privileges, so the daemon
/// drops every capability (permitted, effective and inheritable) and sets
/// no_new_privs on the thread that runs the capture loop.
///
/// Linux keeps capabilities per thread. Threads the .NET runtime started before this
/// call (GC, finalizer, JIT) keep theirs; they never run daemon code. This is defence
/// in depth: the daemon's real safeguard is that it only parses packet headers and
/// forwards bytes, so the code handling untrusted input is small.
/// </summary>
public static class Privileges
{
    [DllImport("libcap.so.2")] static extern IntPtr cap_init();
    [DllImport("libcap.so.2")] static extern int cap_set_proc(IntPtr caps);
    [DllImport("libcap.so.2")] static extern int cap_free(IntPtr caps);
    [DllImport("libcap.so.2")] static extern IntPtr cap_get_proc();
    [DllImport("libcap.so.2")] static extern IntPtr cap_to_text(IntPtr caps, out IntPtr length);
    [DllImport("libc", SetLastError = true)] static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    const int PR_SET_NO_NEW_PRIVS = 38;

    public static void DropAll()
    {
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
            throw new IOException("prctl(PR_SET_NO_NEW_PRIVS) failed: " + Marshal.GetLastPInvokeError());
        var empty = cap_init();
        if (empty == IntPtr.Zero)
            throw new IOException("cap_init failed");
        try
        {
            if (cap_set_proc(empty) != 0)
                throw new IOException("Could not drop capabilities");
        }
        finally
        {
            cap_free(empty);
        }
    }

    /// <summary>Current capabilities as text ("=" means none), for the status line and tests.</summary>
    public static string Current()
    {
        var caps = cap_get_proc();
        if (caps == IntPtr.Zero)
            return "?";
        var text = cap_to_text(caps, out _);
        try
        {
            return Marshal.PtrToStringUTF8(text) ?? "?";
        }
        finally
        {
            cap_free(text);
            cap_free(caps);
        }
    }
}
