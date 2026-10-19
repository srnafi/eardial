using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EarDial;

// Listens for foreground changes. While the shell desktop is the foreground
// window the widget is topmost, so Show Desktop cannot cover it; as soon as a
// normal window takes focus the widget goes to the bottom of the normal band,
// which keeps it above the wallpaper but below other windows.
internal sealed class ShowDesktopWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint OutOfContext = 0x0000;
    private const uint NoActivate = 0x0010;
    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc procedure, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maxLength);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static readonly IntPtr TopmostFlag = new(-1);
    private static readonly IntPtr NoTopmostFlag = new(-2);
    private static readonly IntPtr BottomFlag = new(1);

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly WinEventProc _procedure;

    private IntPtr _hook;

    public ShowDesktopWatcher(Window window, bool manualTopmost)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
        ManualTopmost = manualTopmost;
        _procedure = OnWinEvent;
    }

    // When set the widget stays above everything regardless of focus.
    public bool ManualTopmost { get; set; }

    public void Start()
    {
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _procedure, 0, 0, OutOfContext);
        ApplyManual();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void ApplyManual()
    {
        IntPtr self = new WindowInteropHelper(_window).Handle;
        if (self == IntPtr.Zero) return;

        SetWindowPos(self, ManualTopmost ? TopmostFlag : NoTopmostFlag, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        // The hook fires on the installing thread, which is the UI thread.
        _dispatcher.BeginInvoke(() =>
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            IntPtr self = new WindowInteropHelper(_window).Handle;
            if (foreground == self) return;

            IntPtr shell = GetShellWindow();
            var name = new System.Text.StringBuilder(256);
            GetClassName(foreground, name, name.Capacity);

            bool desktopShown = foreground == shell
                                || name.ToString() is "Progman" or "WorkerW";

            bool topmost = ManualTopmost || desktopShown;

            // Bottom rather than not-topmost: not-topmost would leave the widget
            // above the window that just took focus.
            IntPtr after = topmost ? TopmostFlag : BottomFlag;
            SetWindowPos(self, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        });
    }
}