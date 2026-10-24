using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LeftRightAudioBalancer;

// Listens for foreground changes, minimize and restore. The widget is topmost
// while the desktop is in view, which is what Show Desktop leaves behind. While
// an app holds focus it drops out of the topmost band so it cannot cover that
// app: HWND_BOTTOM is never used, because a window below the desktop windows
// disappears with everything else when Show Desktop runs.
internal sealed class ShowDesktopWatcher : IDisposable
{
    private const uint EventForeground = 0x0003;
    private const uint EventMinStart = 0x0016;
    private const uint EventMinEnd = 0x0017;
    private const uint NoActivate = 0x0010;
    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;
    private const int GwOwner = 4;
    private const int SwShowNoActivate = 4;
    private const int DwmCloaked = 14;

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

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maxLength);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private static readonly IntPtr Topmost = new(-1);
    private static readonly IntPtr NotTopmost = new(-2);

    private readonly Window _window;
    private readonly WinEventProc _proc;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _safetyNet;
    private readonly System.Text.StringBuilder _name = new(128);

    private IntPtr _hook1;
    private IntPtr _hook2;

    // When set the widget stays above everything regardless of what is visible.
    public bool ManualTopmost { get; set; }

    public ShowDesktopWatcher(Window window, bool manualTopmost)
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

            // Foreground changes to a normal app window are applied directly, so
            // the widget never sits over an app, not even briefly.
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

            // Show Desktop animates windows away, so shell transitions settle first.
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

        // A minimized window cannot be raised, so restore first and then decide.
        // Show Desktop minimizes the widget along with everything else.
        if (IsIconic(self)) ShowWindow(self, SwShowNoActivate);

        if (ManualTopmost || DesktopInView())
        {
            SetWindowPos(self, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
        else
        {
            // Not topmost, never bottom: HWND_BOTTOM puts the widget under the
            // desktop windows themselves, which is what made it vanish on Show
            // Desktop and then stay gone once the desktop was in view.
            SetWindowPos(self, NotTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
    }

    // True when the desktop is what the user is looking at. The foreground
    // window is the authority: Show Desktop hands focus to the shell, so a
    // Progman/WorkerW foreground means the desktop, whatever else happens to be
    // visible. The enumeration below is the fallback for the moments when the
    // foreground window is one of our own or has not settled yet.
    private bool DesktopInView()
    {
        IntPtr self = new WindowInteropHelper(_window).Handle;

        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != self)
        {
            _name.Clear();
            GetClassName(foreground, _name, _name.Capacity);
            string front = _name.ToString();

            if (front is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            // A normal app holds focus, so the desktop is not in view even if
            // every other window happens to be minimized.
            if (front is not "Windows.UI.Core.CoreWindow") return false;
        }

        bool appVisible = false;

        EnumWindows((window, _) =>
        {
            if (window == self || !IsWindowVisible(window) || IsIconic(window)) return true;

            // Cloaked windows are managed by DWM but not drawn, so they cannot
            // cover anything. DWMWA_CLOAKED (14) is the attribute that reports
            // this; 13 and 11 return E_INVALIDARG on Windows 10.
            if (DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, 4) == 0 && cloaked != 0)
            {
                return true;
            }

            if (GetWindow(window, GwOwner) != IntPtr.Zero) return true;
            if (GetWindowTextLength(window) == 0) return true;

            int style = GetWindowLong(window, GwlExStyle);
            if ((style & WsExToolWindow) != 0 && (style & WsExAppWindow) == 0) return true;

            _name.Clear();
            GetClassName(window, _name, _name.Capacity);
            string className = _name.ToString();

            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            // The UWP text-input host is resident but never shown to the user.
            if (className == "Windows.UI.Core.CoreWindow") return true;

            appVisible = true;
            return false;
        }, IntPtr.Zero);

        return !appVisible;
    }
}