using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EarDial;

// Keeps the widget visible on an empty desktop without sitting over other
// windows. Windows has no z-order for that, so the widget stays topmost and
// hides itself while the foreground window overlaps it.
internal sealed class VisibilityGuard
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder name, int maxLength);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private bool _covered;

    public VisibilityGuard(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Update();
    }

    public void Start() => _timer.Start();

    private void Update()
    {
        bool covered = IsCoveredByForeground();
        if (covered == _covered) return;

        _covered = covered;
        _window.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
    }

    private bool IsCoveredByForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        IntPtr self = new WindowInteropHelper(_window).Handle;

        if (foreground == self || foreground == IntPtr.Zero) return false;
        if (IsIconic(foreground)) return false;

        // Show Desktop raises the shell's own window; the widget should stay
        // visible then, not hide behind the wallpaper it sits on.
        var className = new System.Text.StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        string name = className.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

        // The widget itself is foreground while it is being used.
        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == (uint)Environment.ProcessId) return false;

        if (!GetWindowRect(foreground, out Rect other)) return false;

        GetWindowRect(self, out Rect mine);

        bool overlap =
            other.Left < mine.Right && other.Right > mine.Left &&
            other.Top < mine.Bottom && other.Bottom > mine.Top;

        return overlap;
    }
}