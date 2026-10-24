using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LeftRightAudioBalancer;

// Keeps the card at the back of the desktop, the way a widget belongs there.
// The card is never topmost: topmost is what let a click on the taskbar or on
// the wallpaper throw it over every open window. Instead it is parked directly
// above the desktop window, which is the bottom of the normal z-order band.
// Show Desktop minimizes everything else, so the card is left on the screen
// by itself; opening a window covers it again. That is the whole illusion, and
// it needs no special case for Win+D, the taskbar or the wallpaper.
internal sealed class DesktopAnchor : IDisposable
{
    private const uint EventForeground = 0x0003;
    private const uint EventMinStart = 0x0016;
    private const uint EventMinEnd = 0x0017;

    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;
    private const uint NoActivate = 0x0010;

    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;

    private const int GwHwndPrev = 3;
    private const int SwShowNoActivate = 4;

    // Guard for the walk up the z-order. The topmost windows that pile up
    // above the desktop are a handful in practice; this just bounds a walk
    // that should end after a step or two.
    private const int MaxWalk = 4096;

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc procedure, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc procedure, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maxLength);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static readonly IntPtr Topmost = new(-1);
    private static readonly IntPtr NotTopmost = new(-2);

    private readonly Window _window;
    private readonly WinEventProc _proc;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _safetyNet;
    private readonly System.Text.StringBuilder _name = new(128);

    private IntPtr _hook1;
    private IntPtr _hook2;

    // When set the card stays above everything regardless of what is visible.
    public bool ManualTopmost { get; set; }

    public DesktopAnchor(Window window, bool manualTopmost)
    {
        _window = window;
        ManualTopmost = manualTopmost;

        // Created before _proc so the callback can never see a null timer.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Apply();
        };

        _safetyNet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _safetyNet.Tick += (_, _) => Apply();

        _proc = (_, _, _, objectId, _, _, _) =>
        {
            if (objectId != 0) return;

            // Foreground changes to a normal app window are applied directly,
            // so the card sinks out of the way without a visible pause.
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                _name.Clear();
                GetClassName(foreground, _name, _name.Capacity);
                string className = _name.ToString();

                if (className is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Core.CoreWindow"))
                {
                    Apply();
                    return;
                }
            }

            // Show Desktop animates windows away, so shell transitions settle
            // first. The card has nowhere to go either way, so this can wait.
            _debounce.Stop();
            _debounce.Start();
        };
    }

    public void Start()
    {
        _hook1 = SetWinEventHook(EventForeground, EventForeground, IntPtr.Zero, _proc, 0, 0, 0);
        _hook2 = SetWinEventHook(EventMinStart, EventMinEnd, IntPtr.Zero, _proc, 0, 0, 0);
        _safetyNet.Start();
        Apply();
    }

    public void Dispose()
    {
        _safetyNet.Stop();
        _debounce.Stop();

        if (_hook1 != IntPtr.Zero) UnhookWinEvent(_hook1);
        if (_hook2 != IntPtr.Zero) UnhookWinEvent(_hook2);

        _hook1 = IntPtr.Zero;
        _hook2 = IntPtr.Zero;
    }

    public void Apply()
    {
        IntPtr self = new WindowInteropHelper(_window).Handle;
        if (self == IntPtr.Zero) return;

        // A minimized window cannot be repositioned, so restore first and then
        // decide. Show Desktop minimizes the card along with everything else.
        if (IsIconic(self)) ShowWindow(self, SwShowNoActivate);

        if (ManualTopmost)
        {
            SetWindowPos(self, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
            return;
        }

        // The card belongs under the windows, never over them, so a topmost
        // style left over from an earlier state is dropped before it is moved.
        if ((GetWindowLong(self, GwlExStyle) & WsExTopmost) != 0)
        {
            SetWindowPos(self, NotTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }

        // While the card itself has focus the user is working in it: dragging
        // a slider, dropping the card somewhere. Sinking it then would drop
        // it behind whatever it is being used over, mid-gesture. As soon as
        // focus moves anywhere else the card goes back to the back.
        if (GetForegroundWindow() == self) return;

        SinkToDesktop(self);
    }

    // Parks the card directly above the desktop window: under every other
    // window, over the wallpaper. Returns without touching anything if the
    // desktop cannot be found, which is safer than guessing.
    private void SinkToDesktop(IntPtr self)
    {
        IntPtr desktop = FindDesktopWindow(self);
        if (desktop == IntPtr.Zero) return;

        // The card sits immediately after (below) the window above the
        // desktop, which is the same as sitting directly above the desktop.
        IntPtr above = GetWindow(desktop, GwHwndPrev);

        // Anything already in the topmost band is skipped. Inserting under
        // one of those would drag the card into the band, which is the
        // behaviour this class exists to avoid.
        for (int i = 0; above != IntPtr.Zero && above != self && IsTopmost(above) && i < MaxWalk; i++)
        {
            above = GetWindow(above, GwHwndPrev);
        }

        // Already parked, or the desktop really is at the very top of the
        // z-order, in which case there is nowhere sensible to sit.
        if (above == IntPtr.Zero || above == self) return;

        SetWindowPos(self, above, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    // The first visible Progman or WorkerW in z-order, which is the top of
    // the desktop group. Windows keeps a pile of hidden WorkerW helpers
    // scattered through the list, so the visibility check is what picks the
    // real desktop out of them.
    private IntPtr FindDesktopWindow(IntPtr self)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((window, _) =>
        {
            if (window == self || !IsWindowVisible(window)) return true;

            _name.Clear();
            GetClassName(window, _name, _name.Capacity);
            string className = _name.ToString();

            if (className is not ("Progman" or "WorkerW")) return true;

            found = window;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static bool IsTopmost(IntPtr window) => (GetWindowLong(window, GwlExStyle) & WsExTopmost) != 0;
}
