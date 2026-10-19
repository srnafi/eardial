using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EarDial;

// Listens for foreground changes, minimize and restore. The widget is topmost
// while no real app window is visible, which is what Show Desktop produces, and
// sits at the bottom of the normal band the rest of the time.
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
    private const int DwmCloakedAttribute = 13;
    private const int DwmCloakedShield = 11;

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
    private static readonly IntPtr Bottom = new(1);

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

        bool top = ManualTopmost || DesktopInView();

        if (top)
        {
            // Show Desktop can minimize the widget before this runs.
            if (IsIconic(self)) ShowWindow(self, SwShowNoActivate);

            SetWindowPos(self, Topmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
        else
        {
            SetWindowPos(self, Bottom, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
    }

    // True when no real app window is visible, in other words the desktop.
    private bool DesktopInView()
    {
        IntPtr self = new WindowInteropHelper(_window).Handle;
        bool appVisible = false;

        EnumWindows((window, _) =>
        {
            if (window == self || !IsWindowVisible(window) || IsIconic(window)) return true;

            // Cloaked windows are managed by DWM but not drawn, so they cannot
            // cover anything. Attribute 11 catches shell-cloaked surfaces that
            // attribute 13 does not report on Windows 10.
            if ((DwmGetWindowAttribute(window, DwmCloakedAttribute, out int cloaked, 4) == 0 && cloaked != 0)
                || (DwmGetWindowAttribute(window, DwmCloakedShield, out int shielded, 4) == 0 && shielded != 0))
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