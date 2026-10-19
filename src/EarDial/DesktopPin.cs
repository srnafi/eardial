using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EarDial;

// Parents the widget to the WorkerW window that sits behind the desktop icons,
// so it behaves like part of the wallpaper instead of a floating window.
internal static class DesktopPin
{
    private const uint SpawnWorkerMessage = 0x052C;

    private delegate bool EnumProc(IntPtr handle, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    public static bool Attach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr worker = FindWorkerWindow();

        LastError = 0;

        if (worker == IntPtr.Zero)
        {
            LastError = -1;
            return false;
        }

        IntPtr previous = SetParent(handle, worker);
        if (previous == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        return true;
    }

    public static int LastError { get; private set; }

    public static void Detach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        SetParent(handle, IntPtr.Zero);
    }

    private static IntPtr FindWorkerWindow()
    {
        // Asks Explorer to create the wallpaper layer. Absent Progman this is a
        // no-op and the enumeration below still finds the icon host.
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            SendMessageTimeout(progman, SpawnWorkerMessage, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr worker = IntPtr.Zero;

        EnumWindows((handle, _) =>
        {
            // The window that hosts the desktop icons.
            if (FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;

            // Top-level WorkerW that follows it in z-order is the layer behind the icons.
            IntPtr behind = FindWindowEx(IntPtr.Zero, handle, "WorkerW", null);
            if (behind != IntPtr.Zero) worker = behind;

            return false;
        }, IntPtr.Zero);

        return worker;
    }
}